using AiLibrarian.Eval.Loader;

namespace AiLibrarian.Eval.Runner;

/// <summary>
/// Wires the eval threshold checks to the actual on-disk golden corpus.
///
/// <para><b>Why this exists.</b> Before this file, the Quality Gate
/// workflow ran <c>dotnet test</c> against <c>AiLibrarian.Eval</c> and
/// passed even when the eval would have failed thresholds — because the
/// only assertions of <see cref="EvalReport.MeetsAbsoluteThresholds"/>
/// lived in <see cref="EvalRunnerTests"/>, which fed the runner
/// hand-crafted outcomes designed to fail (to prove the method returns
/// <c>false</c> on bad input). The corpus was never actually loaded and
/// scored against the floors. So Gate #1 of the hardening plan ("eval
/// thresholds") was structurally dormant: a metric-math suite, not a
/// regression detector.</para>
///
/// <para><b>What this file does.</b> Three tests:
/// <list type="number">
///   <item><description><b>Happy-path floor</b> — load every YAML under
///   <c>golden-sets/engineering/</c>, run them through a stub backend
///   that respects each case (returns expected chunks, refuses on
///   must-refuse, cites every claim), assert the report passes the
///   absolute thresholds in <see cref="EvalThresholds"/>. If this fails
///   the corpus or the thresholds are misconfigured, NOT
///   retrieval/synthesis. It's the "wiring is intact" floor.</description></item>
///   <item><description><b>Failed-refusal regression</b> — same corpus,
///   stub that never refuses. The 0.90 refusal floor must trip. This
///   simulates the most dangerous class of regression: AskGuard stops
///   refusing on must-refuse queries (a data-leak path).</description></item>
///   <item><description><b>Uncited-claim regression</b> — same corpus,
///   stub that emits claims without citations. The 0.95 citation
///   coverage floor must trip. Simulates a synthesis path that drops
///   the citation contract.</description></item>
/// </list>
/// </para>
///
/// <para><b>What this file deliberately does NOT do.</b> The stubs are
/// pure functions of the golden case — they never call real retrieval
/// or synthesis code. So a regression in
/// <c>PersonaAwareHybridChunkSearch</c> or <c>AskGuard</c> still won't
/// be caught here. Catching those needs the eval to run against a real
/// API (via <see cref="HttpEvalBackend"/>), which needs the
/// docker-compose dev stack on the deferred slice. This file is the
/// stake-in-the-ground that the gate <em>plumbing</em> works, and the
/// foundation the compose-stack slice lifts onto a real backend.</para>
/// </summary>
public sealed class GoldenCorpusGateTests
{
	private static string CorpusDir
		=> Path.Combine(AppContext.BaseDirectory, "golden-sets", "engineering");

	[Fact]
	public async Task Engineering_corpus_passes_absolute_thresholds_against_healthy_stub()
	{
		var cases = GoldenCaseLoader.LoadAll(CorpusDir);
		cases.Should().NotBeEmpty(
			because: "the engineering golden corpus must ship with the test project; "
				+ "AiLibrarian.Eval.csproj has <None Update=\"golden-sets\\**\\*.yaml\"/> "
				+ "CopyToOutputDirectory=PreserveNewest. An empty load almost always means "
				+ "the YAML files were dropped, not that the corpus is intentionally empty.");

		var mustRefuseCount = cases.Count(c => c.MustRefuse);
		var positiveCount = cases.Count - mustRefuseCount;
		mustRefuseCount.Should().BeGreaterThan(0,
			because: "without a must-refuse case the refusal gate can't be evaluated.");
		positiveCount.Should().BeGreaterThan(0,
			because: "without a positive case citation coverage can't be evaluated.");

		var runner = new EvalRunner();
		var report = await runner.RunAsync(cases, HealthyStub);

		report.MeetsAbsoluteThresholds(new EvalThresholds()).Should().BeTrue(
			because: "the healthy stub respects every case in the corpus, so the gate "
				+ $"must pass; got coverage={report.CitationCoverage:F3} "
				+ $"refusal={report.RefusalRate:F3}.");
	}

	[Fact]
	public async Task Failed_refusal_on_corpus_fails_the_gate()
	{
		// Simulates AskGuard losing its must-refuse behavior. The gate
		// MUST catch this because a missed refusal is a data-exfiltration
		// shaped failure -- the most consequential class of regression
		// the harness exists to guard against.
		var cases = GoldenCaseLoader.LoadAll(CorpusDir);
		cases.Count(c => c.MustRefuse).Should().BeGreaterThan(0);

		var runner = new EvalRunner();
		var report = await runner.RunAsync(cases, NoRefusalStub);

		report.RefusalRate.Should().Be(0.0,
			because: "every must-refuse case was answered instead of refused.");
		report.MeetsAbsoluteThresholds(new EvalThresholds()).Should().BeFalse(
			because: "RefusalRate=0 is well below the 0.90 floor.");
	}

	[Fact]
	public async Task Missing_citations_on_corpus_fail_the_gate()
	{
		// Simulates the synthesis path emitting claims without
		// supporting citations -- a violation of the ADR 0007 citation
		// contract. CitationCoverage = 0 must trip the 0.95 floor.
		var cases = GoldenCaseLoader.LoadAll(CorpusDir);
		cases.Count(c => !c.MustRefuse).Should().BeGreaterThan(0);

		var runner = new EvalRunner();
		var report = await runner.RunAsync(cases, UncitedClaimStub);

		report.CitationCoverage.Should().BeLessThan(0.95,
			because: "the stub emits one uncited claim per positive case.");
		report.MeetsAbsoluteThresholds(new EvalThresholds()).Should().BeFalse(
			because: "CitationCoverage below 0.95 must fail the gate.");
	}

	// --- stub backends ---

	/// <summary>
	/// "If the system were behaving correctly today" backend. Returns
	/// the case's expected chunks for positive cases (so recall = 1.0),
	/// refuses on must-refuse cases, and cites every claim. Used to
	/// prove the gate doesn't fail when nothing is wrong.
	/// </summary>
	private static Task<EvalCaseOutcome> HealthyStub(GoldenCase c, CancellationToken _)
	{
		if (c.MustRefuse)
		{
			return Task.FromResult(new EvalCaseOutcome(
				RetrievedChunkIds: Array.Empty<Guid>(),
				ClaimCount: 0,
				CitedClaimCount: 0,
				Refused: true,
				TokensUsed: 0));
		}

		return Task.FromResult(new EvalCaseOutcome(
			RetrievedChunkIds: c.ExpectedChunkIds.ToArray(),
			ClaimCount: 1,
			CitedClaimCount: 1,
			Refused: false,
			TokensUsed: 0));
	}

	/// <summary>
	/// Regression simulator: every case (including must-refuse ones)
	/// gets a normal answer. Drives <see cref="EvalReport.RefusalRate"/>
	/// to 0 across the corpus.
	/// </summary>
	private static Task<EvalCaseOutcome> NoRefusalStub(GoldenCase c, CancellationToken _)
		=> Task.FromResult(new EvalCaseOutcome(
			RetrievedChunkIds: c.ExpectedChunkIds.ToArray(),
			ClaimCount: 1,
			CitedClaimCount: 1,
			Refused: false,
			TokensUsed: 0));

	/// <summary>
	/// Regression simulator: positive cases emit a claim with no
	/// citation; must-refuse cases still refuse correctly (we're
	/// isolating the citation-coverage gate from the refusal gate).
	/// Drives <see cref="EvalReport.CitationCoverage"/> to 0 across the
	/// positive subset.
	/// </summary>
	private static Task<EvalCaseOutcome> UncitedClaimStub(GoldenCase c, CancellationToken _)
	{
		if (c.MustRefuse)
		{
			return Task.FromResult(new EvalCaseOutcome(
				RetrievedChunkIds: Array.Empty<Guid>(),
				ClaimCount: 0,
				CitedClaimCount: 0,
				Refused: true,
				TokensUsed: 0));
		}

		return Task.FromResult(new EvalCaseOutcome(
			RetrievedChunkIds: c.ExpectedChunkIds.ToArray(),
			ClaimCount: 1,
			CitedClaimCount: 0,
			Refused: false,
			TokensUsed: 0));
	}
}
