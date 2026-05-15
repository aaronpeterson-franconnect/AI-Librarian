using AiLibrarian.Eval.Metrics;

namespace AiLibrarian.Eval.Runner;

/// <summary>
/// Orchestrates a golden-set run. Pluggable backend: callers supply
/// a <see cref="RetrievalBackend"/> that, given a case, produces a
/// retrieval ranking and an optional synthesis outcome. The runner
/// computes recall@k, MRR, nDCG@k, citation coverage, and refusal-
/// rate metrics across the case set and aggregates them into an
/// <see cref="EvalReport"/>.
///
/// <para>
/// Phase 1 mock-first contract: pass a deterministic backend that
/// returns canned retrieval rankings for unit tests, or wire to
/// <c>HybridChunkSearch</c> via a <c>WebApplicationFactory&lt;Program&gt;</c>
/// for real-traffic exercise. The runner doesn't care which.
/// </para>
/// </summary>
public sealed class EvalRunner
{
	private readonly EvalRunnerOptions _options;

	/// <summary>Creates the runner with default thresholds.</summary>
	public EvalRunner()
		: this(new EvalRunnerOptions())
	{
	}

	/// <summary>Creates the runner with custom thresholds (CI gates).</summary>
	public EvalRunner(EvalRunnerOptions options)
	{
		_options = options;
	}

	/// <summary>
	/// Run every golden case through <paramref name="backend"/> and
	/// produce an aggregated <see cref="EvalReport"/>.
	/// </summary>
	public async Task<EvalReport> RunAsync(
		IReadOnlyList<GoldenCase> cases,
		RetrievalBackend backend,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(cases);
		ArgumentNullException.ThrowIfNull(backend);

		if (cases.Count == 0)
		{
			return EvalReport.Empty();
		}

		var perCase = new List<EvalCaseResult>(cases.Count);
		var recallSum = 0.0;
		var mrrSum = 0.0;
		var ndcgSum = 0.0;
		var citationCovSum = 0.0;
		var totalClaims = 0;
		var citedClaims = 0;
		var mustRefuseTotal = 0;
		var actuallyRefused = 0;
		long totalTokens = 0;

		foreach (var c in cases)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var outcome = await backend(c, cancellationToken).ConfigureAwait(false);
			var relevant = c.ExpectedChunkIds.ToHashSet();

			var recall = RetrievalMetrics.RecallAtK(outcome.RetrievedChunkIds, relevant, _options.RecallK);
			var rr = RetrievalMetrics.ReciprocalRank(outcome.RetrievedChunkIds, relevant);
			var ndcg = RetrievalMetrics.NDcgAtK(outcome.RetrievedChunkIds, c.RelevanceGrades(), _options.RecallK);

			recallSum += recall;
			mrrSum += rr;
			ndcgSum += ndcg;

			totalClaims += outcome.ClaimCount;
			citedClaims += outcome.CitedClaimCount;
			citationCovSum += SynthesisMetrics.CitationCoverage(outcome.ClaimCount, outcome.CitedClaimCount);
			totalTokens += outcome.TokensUsed;

			if (c.MustRefuse)
			{
				mustRefuseTotal++;
				if (outcome.Refused)
				{
					actuallyRefused++;
				}
			}

			perCase.Add(new EvalCaseResult(
				CaseId: c.Id,
				Persona: c.Persona,
				RecallAtK: recall,
				ReciprocalRank: rr,
				NDcgAtK: ndcg,
				ClaimCount: outcome.ClaimCount,
				CitedClaimCount: outcome.CitedClaimCount,
				Refused: outcome.Refused));
		}

		var caseCount = cases.Count;
		return new EvalReport(
			CaseCount: caseCount,
			RecallAtKAverage: recallSum / caseCount,
			MeanReciprocalRank: mrrSum / caseCount,
			NDcgAtKAverage: ndcgSum / caseCount,
			CitationCoverage: SynthesisMetrics.CitationCoverage(totalClaims, citedClaims),
			RefusalRate: SynthesisMetrics.RefusalRate(mustRefuseTotal, actuallyRefused),
			TokensPerCase: SynthesisMetrics.TokensPerCase(totalTokens, caseCount),
			RecallK: _options.RecallK,
			Cases: perCase);
	}
}

/// <summary>
/// Backend contract: given a golden case, produce the system's
/// actual response. Wire to <c>HybridChunkSearch</c> + an LLM call
/// for real evaluation, or to a deterministic stub for unit tests.
/// </summary>
public delegate Task<EvalCaseOutcome> RetrievalBackend(GoldenCase goldenCase, CancellationToken cancellationToken);

/// <summary>The system's response to one golden case.</summary>
/// <param name="RetrievedChunkIds">Retrieved chunk IDs in rank order.</param>
/// <param name="ClaimCount">Number of claims in the synthesized answer; zero for retrieval-only runs.</param>
/// <param name="CitedClaimCount">Of those, how many carried at least one citation.</param>
/// <param name="Refused">True when the system declined to answer (used against <see cref="GoldenCase.MustRefuse"/>).</param>
/// <param name="TokensUsed">Prompt + completion tokens; zero for retrieval-only runs.</param>
public sealed record EvalCaseOutcome(
	IReadOnlyList<Guid> RetrievedChunkIds,
	int ClaimCount,
	int CitedClaimCount,
	bool Refused,
	long TokensUsed);

/// <summary>Tunable thresholds + cutoffs for the runner.</summary>
public sealed class EvalRunnerOptions
{
	/// <summary>Top-k cutoff for recall@k and nDCG@k. Default 10 matches the CI gate.</summary>
	public int RecallK { get; set; } = 10;
}
