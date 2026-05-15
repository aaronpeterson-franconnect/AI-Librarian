using System.Text.Json;
using System.Text.Json.Serialization;

using AiLibrarian.Domain.Citations;
using AiLibrarian.Eval.Metrics;

namespace AiLibrarian.Eval.Calibration;

/// <summary>
/// Emits a deterministic JSON projection of a
/// <see cref="CalibrationReport"/> for CI workflows to consume.
/// The shape is stable: the workflow's <c>jq</c> queries depend on
/// the field names below.
///
/// <para>Contents:
/// <list type="bullet">
///   <item>Top-level summary: total cases, observed agreement, Cohen's κ, threshold band.</item>
///   <item>Confusion matrix flattened to a list of
///         <c>{ humanVerdict, judgeVerdict, count }</c> rows so consumers
///         can drill into systematic disagreements.</item>
///   <item>Per-case outcomes: case id, human + judge verdicts, judge
///         confidence + rationale. Useful for post-mortem on
///         high-confidence disagreements.</item>
/// </list>
/// </para>
///
/// <para>The κ threshold band classification is documented in
/// <c>docs/eval/calibration-rubric.md</c>:
/// <c>NearPerfect</c> (≥0.8) / <c>Substantial</c> (≥0.7) /
/// <c>Moderate</c> (≥0.4) / <c>Poor</c> (&lt;0.4). The workflow
/// turns this string into a green / yellow / red annotation.</para>
/// </summary>
public static class CalibrationReportWriter
{
	private static readonly JsonSerializerOptions WriterOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	/// <summary>
	/// Render the report as a deterministic JSON string.
	/// </summary>
	public static string ToJson(CalibrationReport report, DateTimeOffset generatedAt)
	{
		ArgumentNullException.ThrowIfNull(report);

		var humanLabels = report.Outcomes.Select(o => o.Case.HumanVerdict).ToArray();
		var judgeLabels = report.Outcomes.Select(o => o.JudgeGrade.Verdict).ToArray();
		var matrix = humanLabels.Length == 0
			? new int[4, 4]
			: CohenKappa.ConfusionMatrix(humanLabels, judgeLabels);

		var matrixRows = new List<ConfusionRow>(16);
		foreach (var human in EnumValues())
		{
			foreach (var judge in EnumValues())
			{
				var count = matrix[(int)human, (int)judge];
				if (count > 0)
				{
					matrixRows.Add(new ConfusionRow(human.ToString(), judge.ToString(), count));
				}
			}
		}

		var dto = new ReportDto(
			GeneratedAt: generatedAt,
			TotalCases: report.Outcomes.Count,
			Agreements: report.Outcomes.Count(o => o.Agreed),
			Disagreements: report.Outcomes.Count(o => !o.Agreed),
			ObservedAgreement: Round(report.ObservedAgreement),
			CohenKappa: double.IsNaN(report.CohenKappa) ? 0.0 : Round(report.CohenKappa),
			Band: ClassifyBand(report.CohenKappa),
			Confusion: matrixRows,
			Cases: report.Outcomes.Select(o => new CaseRow(
				o.Case.Id,
				o.Case.HumanVerdict.ToString(),
				o.JudgeGrade.Verdict.ToString(),
				Round(o.JudgeGrade.Confidence),
				Truncate(o.JudgeGrade.Rationale, 280),
				o.Agreed)).ToList());

		return JsonSerializer.Serialize(dto, WriterOptions);
	}

	/// <summary>Write the JSON report to <paramref name="path"/>. Overwrites if present.</summary>
	public static async Task WriteAsync(
		CalibrationReport report,
		string path,
		DateTimeOffset generatedAt,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		var json = ToJson(report, generatedAt);
		await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Classify κ into the four bands the rubric documents. Public so
	/// the workflow's annotation step can rely on the same logic.
	/// </summary>
	public static string ClassifyBand(double kappa)
	{
		if (double.IsNaN(kappa))
		{
			return "Unknown";
		}
		if (kappa >= 0.8)
		{
			return "NearPerfect";
		}
		if (kappa >= 0.7)
		{
			return "Substantial";
		}
		if (kappa >= 0.4)
		{
			return "Moderate";
		}
		return "Poor";
	}

	private static double Round(double value)
		=> Math.Round(value, digits: 4, MidpointRounding.AwayFromZero);

	private static string Truncate(string? value, int max)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}
		return value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
	}

	private static ClaimVerdict[] EnumValues()
		=> Enum.GetValues<ClaimVerdict>();

	private sealed record ReportDto(
		DateTimeOffset GeneratedAt,
		int TotalCases,
		int Agreements,
		int Disagreements,
		double ObservedAgreement,
		double CohenKappa,
		string Band,
		IReadOnlyList<ConfusionRow> Confusion,
		IReadOnlyList<CaseRow> Cases);

	private sealed record ConfusionRow(string HumanVerdict, string JudgeVerdict, int Count);

	private sealed record CaseRow(
		string Id,
		string HumanVerdict,
		string JudgeVerdict,
		double JudgeConfidence,
		string JudgeRationale,
		bool Agreed);
}
