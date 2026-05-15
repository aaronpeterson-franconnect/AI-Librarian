namespace AiLibrarian.Security;

/// <summary>
/// Operational support for the <see cref="SecretRedactionMode.Shadow"/>
/// → <see cref="SecretRedactionMode.Enforce"/> flip. Per ADR 0017 the
/// flip is gated on per-tenant precision sampling reaching ≥0.9.
/// This file provides the helper types + math for tracking it:
/// <list type="bullet">
///   <item><see cref="LabeledCandidate"/> — one operator-labeled
///         candidate row (true positive vs. false positive).</item>
///   <item><see cref="PrecisionSampling"/> — aggregates labels into
///         per-kind precision + an enforce-readiness verdict.</item>
/// </list>
///
/// <para>The labeling step is operator-driven: the redactor in shadow
/// mode emits candidates to the audit log; an operator pulls them via
/// <c>deploy/scripts/Export-RedactionCandidates.ps1</c>, labels them in
/// a CSV, then feeds the labels back through
/// <see cref="PrecisionSampling.Compute"/>. The verdict gates whether
/// the operator may flip <c>Mcp:AskGuard:RedactionMode</c> to
/// <c>Enforce</c>.</para>
/// </summary>
public sealed record LabeledCandidate(
	string Kind,
	bool IsTruePositive);

/// <summary>Aggregated precision metrics + an enforce-readiness verdict.</summary>
/// <param name="OverallPrecision">TP / (TP + FP) across all kinds.</param>
/// <param name="PerKind">One row per detected kind.</param>
/// <param name="TotalLabeled">Total number of labeled samples.</param>
/// <param name="EnforceReady">True only when <see cref="OverallPrecision"/> ≥ <c>precisionFloor</c>, the sample is large enough, AND every kind clears the floor.</param>
/// <param name="Reasons">Human-readable reasons the verdict is what it is.</param>
public sealed record PrecisionReport(
	double OverallPrecision,
	IReadOnlyList<KindPrecision> PerKind,
	int TotalLabeled,
	bool EnforceReady,
	IReadOnlyList<string> Reasons);

/// <summary>Per-kind precision row.</summary>
/// <param name="Kind">The redactor's pattern kind (e.g. <c>jwt</c>, <c>aws_access_key</c>).</param>
/// <param name="TruePositives">Labels marked as real credentials.</param>
/// <param name="FalsePositives">Labels marked as not-a-credential (e.g. UUIDs).</param>
/// <param name="Precision">TP / (TP + FP); 1.0 when no samples (the kind never fired).</param>
public sealed record KindPrecision(
	string Kind,
	int TruePositives,
	int FalsePositives,
	double Precision);

/// <summary>Static aggregator for precision-sampling labels.</summary>
public static class PrecisionSampling
{
	/// <summary>
	/// Aggregate per-kind precision and decide whether enforce-mode is
	/// ready.
	/// </summary>
	/// <param name="labels">Operator-labeled candidates.</param>
	/// <param name="precisionFloor">
	/// Per-kind minimum precision required to flip to enforce. ADR 0017
	/// names 0.9 as the default.
	/// </param>
	/// <param name="minSampleSize">
	/// Overall minimum sample count below which the verdict is "not yet
	/// ready" regardless of precision. Default 100 — small samples are
	/// noisy and shouldn't gate a behavior flip.
	/// </param>
	public static PrecisionReport Compute(
		IReadOnlyCollection<LabeledCandidate> labels,
		double precisionFloor = 0.9,
		int minSampleSize = 100)
	{
		ArgumentNullException.ThrowIfNull(labels);

		if (precisionFloor < 0.0 || precisionFloor > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(precisionFloor), precisionFloor, "Must be in [0, 1].");
		}

		var perKind = new Dictionary<string, (int Tp, int Fp)>(StringComparer.OrdinalIgnoreCase);
		var totalTp = 0;
		var totalFp = 0;

		foreach (var label in labels)
		{
			var kind = string.IsNullOrWhiteSpace(label.Kind) ? "unknown" : label.Kind;
			var (tp, fp) = perKind.GetValueOrDefault(kind);
			if (label.IsTruePositive)
			{
				tp++;
				totalTp++;
			}
			else
			{
				fp++;
				totalFp++;
			}

			perKind[kind] = (tp, fp);
		}

		var perKindRows = perKind
			.OrderBy(kv => kv.Key, StringComparer.Ordinal)
			.Select(kv =>
			{
				var (tp, fp) = kv.Value;
				var p = tp + fp == 0 ? 1.0 : (double)tp / (tp + fp);
				return new KindPrecision(kv.Key, tp, fp, p);
			})
			.ToArray();

		var overall = totalTp + totalFp == 0 ? 1.0 : (double)totalTp / (totalTp + totalFp);
		var totalLabeled = labels.Count;

		var reasons = new List<string>();
		var ready = true;

		if (totalLabeled < minSampleSize)
		{
			ready = false;
			reasons.Add($"Sample size {totalLabeled} below minimum {minSampleSize}.");
		}

		if (overall < precisionFloor)
		{
			ready = false;
			reasons.Add($"Overall precision {overall:F3} below floor {precisionFloor:F3}.");
		}

		foreach (var row in perKindRows)
		{
			if (row.TruePositives + row.FalsePositives == 0)
			{
				// Kind never fired in the sample -- not a blocker; just
				// note that we have no signal for it.
				reasons.Add($"Kind '{row.Kind}' has no samples; precision unknown.");
				continue;
			}

			if (row.Precision < precisionFloor)
			{
				ready = false;
				reasons.Add($"Kind '{row.Kind}' precision {row.Precision:F3} below floor {precisionFloor:F3} (TP={row.TruePositives}, FP={row.FalsePositives}).");
			}
		}

		if (ready)
		{
			reasons.Add($"All gates met: overall {overall:F3} >= {precisionFloor:F3}, per-kind {precisionFloor:F3}+, sample size {totalLabeled} >= {minSampleSize}.");
		}

		return new PrecisionReport(
			OverallPrecision: overall,
			PerKind: perKindRows,
			TotalLabeled: totalLabeled,
			EnforceReady: ready,
			Reasons: reasons);
	}
}
