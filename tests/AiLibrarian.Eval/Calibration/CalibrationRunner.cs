using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Eval.Metrics;

namespace AiLibrarian.Eval.Calibration;

/// <summary>
/// Runs the LLM judge over a calibration set and computes
/// human-vs-judge agreement via Cohen's κ. The harness's nightly
/// workflow invokes this and emits the κ value to the
/// <c>judge_inter_rater_agreement</c> CI gate (warn at &lt; 0.7 per the
/// hardening plan).
///
/// <para>The runner is deliberately stateless and synchronous-friendly:
/// pass in cases + a grader, get a <see cref="CalibrationReport"/>
/// back. No I/O outside the grader's LLM call.</para>
/// </summary>
public static class CalibrationRunner
{
	/// <summary>
	/// Grade every <paramref name="cases"/> entry with
	/// <paramref name="grader"/>, then aggregate.
	/// </summary>
	public static async Task<CalibrationReport> RunAsync(
		IReadOnlyList<CalibrationCase> cases,
		IClaimGrader grader,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(cases);
		ArgumentNullException.ThrowIfNull(grader);

		if (cases.Count == 0)
		{
			return new CalibrationReport(
				Outcomes: Array.Empty<CalibrationOutcome>(),
				CohenKappa: 0.0,
				ObservedAgreement: 0.0);
		}

		var outcomes = new List<CalibrationOutcome>(cases.Count);
		foreach (var c in cases)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var claim = BuildClaim(c);
			var citedTexts = new Dictionary<Guid, string>(c.CitedChunks.Count);
			foreach (var chunk in c.CitedChunks)
			{
				citedTexts[chunk.ChunkId] = chunk.Text;
			}

			ClaimGrade judgeGrade;
			try
			{
				judgeGrade = await grader
					.GradeAsync(claim, citedTexts, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// A grader exception is a calibration failure -- record it
				// as Unverifiable so the run still completes; the κ
				// computation sees it as a disagreement when the human
				// chose anything else.
				judgeGrade = new ClaimGrade(
					claim.Id,
					ClaimVerdict.Unverifiable,
					Confidence: 0.0,
					Rationale: $"Grader threw: {ex.GetType().Name}: {ex.Message}");
			}

			outcomes.Add(new CalibrationOutcome(
				Case: c,
				JudgeGrade: judgeGrade));
		}

		var humanLabels = outcomes.Select(o => o.Case.HumanVerdict).ToArray();
		var judgeLabels = outcomes.Select(o => o.JudgeGrade.Verdict).ToArray();

		var kappa = CohenKappa.Compute(humanLabels, judgeLabels);
		var observed = ComputeObservedAgreement(humanLabels, judgeLabels);

		return new CalibrationReport(
			Outcomes: outcomes,
			CohenKappa: kappa,
			ObservedAgreement: observed);
	}

	private static Claim BuildClaim(CalibrationCase c)
	{
		// Synthetic claim id; the grader doesn't persist anything, but
		// the verdict carries the id forward so the runner can stitch
		// outcomes back to cases by index.
		var claimId = Guid.NewGuid();
		var citations = new List<Citation>(c.CitedChunks.Count);
		foreach (var chunk in c.CitedChunks)
		{
			// Span covers the whole chunk -- the grader's prompt builder
			// will clamp this against the actual text length.
			citations.Add(new Citation(
				Id: Guid.NewGuid(),
				ChunkId: chunk.ChunkId,
				SpanStart: 0,
				SpanEnd: chunk.Text.Length,
				Confidence: 1.0));
		}

		return new Claim(
			Id: claimId,
			Text: c.ClaimText,
			FacetClassification: Classification.Internal,
			Citations: citations);
	}

	private static double ComputeObservedAgreement(
		ClaimVerdict[] human,
		ClaimVerdict[] judge)
	{
		if (human.Length == 0)
		{
			return 0.0;
		}
		var agreed = 0;
		for (var i = 0; i < human.Length; i++)
		{
			if (human[i] == judge[i])
			{
				agreed++;
			}
		}
		return (double)agreed / human.Length;
	}
}

/// <summary>One human-vs-judge pairing inside a calibration run.</summary>
/// <param name="Case">The calibration case (carries the human gold).</param>
/// <param name="JudgeGrade">What the LLM judge returned.</param>
public sealed record CalibrationOutcome(
	CalibrationCase Case,
	ClaimGrade JudgeGrade)
{
	/// <summary>True when human and judge agree on the verdict.</summary>
	public bool Agreed => Case.HumanVerdict == JudgeGrade.Verdict;
}

/// <summary>Aggregate report of one calibration run.</summary>
/// <param name="Outcomes">One entry per calibration case, in input order.</param>
/// <param name="CohenKappa">Inter-rater agreement; see <see cref="CohenKappa"/>.</param>
/// <param name="ObservedAgreement">Plain agreement share (no chance correction). Mostly diagnostic — κ is the gate.</param>
public sealed record CalibrationReport(
	IReadOnlyList<CalibrationOutcome> Outcomes,
	double CohenKappa,
	double ObservedAgreement);
