using AiLibrarian.Domain;
using AiLibrarian.Eval.Metrics;

namespace AiLibrarian.Eval.Runner;

/// <summary>
/// Pins the delta arithmetic and the "persona made it better /
/// worse / no change" bookkeeping the persona-aware retrieval gate
/// depends on. Backends are stubs; the persona profile / reranker
/// integration is exercised in Infrastructure.Tests separately.
/// </summary>
public sealed class PersonaDeltaRunnerTests
{
	private static readonly Guid ChunkA = new("11111111-1111-1111-1111-aaaaaaaaaaaa");
	private static readonly Guid ChunkB = new("11111111-1111-1111-1111-bbbbbbbbbbbb");
	private static readonly Guid ChunkC = new("11111111-1111-1111-1111-cccccccccccc");
	private static readonly Guid ChunkD = new("11111111-1111-1111-1111-dddddddddddd");

	private static GoldenCase Case(string id, params Guid[] expected)
		=> new(
			Id: id,
			Query: "anything",
			Persona: "engineering",
			ClassificationScope: Classification.Internal,
			ExpectedChunkIds: expected,
			ExpectedCitations: [],
			MustRefuse: false,
			Tags: new Dictionary<string, string>(StringComparer.Ordinal));

	private static RetrievalBackend StubRanking(params Guid[] retrieved)
		=> (_, _) => Task.FromResult(new EvalCaseOutcome(
			RetrievedChunkIds: retrieved,
			ClaimCount: 0,
			CitedClaimCount: 0,
			Refused: false,
			TokensUsed: 0));

	[Fact]
	public async Task Empty_case_list_returns_empty_report_with_persona_stamped()
	{
		var runner = new PersonaDeltaRunner();
		var report = await runner.RunAsync(
			Array.Empty<GoldenCase>(),
			StubRanking(),
			StubRanking(),
			"engineering");

		report.CaseCount.Should().Be(0);
		report.Persona.Should().Be("engineering");
		report.Cases.Should().BeEmpty();
	}

	[Fact]
	public async Task Identical_backends_produce_zero_delta()
	{
		// Neutral and persona return the same ranking. Every delta = 0;
		// every case lands in "unchanged". This is the regression
		// invariant: persona = neutral profile shouldn't shift anything.
		var runner = new PersonaDeltaRunner();
		var ranking = StubRanking(ChunkA, ChunkB, ChunkC);

		var report = await runner.RunAsync(
			[Case("c1", ChunkA, ChunkB)],
			ranking,
			ranking,
			"engineering");

		report.RecallDeltaAverage.Should().Be(0.0);
		report.MeanReciprocalRankDelta.Should().Be(0.0);
		report.NDcgDeltaAverage.Should().Be(0.0);
		report.CasesImproved.Should().Be(0);
		report.CasesDegraded.Should().Be(0);
		report.CasesUnchanged.Should().Be(1);
		report.TopOneChangedCount.Should().Be(0);
	}

	[Fact]
	public async Task Persona_improves_recall_when_it_surfaces_a_missed_chunk()
	{
		// Neutral misses ChunkB; persona surfaces it -> recall delta > 0.
		var runner = new PersonaDeltaRunner();

		var report = await runner.RunAsync(
			[Case("c1", ChunkA, ChunkB)],
			neutralBackend: StubRanking(ChunkA, ChunkC, ChunkD),
			personaBackend: StubRanking(ChunkA, ChunkB, ChunkC),
			"engineering");

		report.NeutralRecallAtKAverage.Should().Be(0.5);
		report.PersonaRecallAtKAverage.Should().Be(1.0);
		report.RecallDeltaAverage.Should().Be(0.5);
		report.CasesImproved.Should().Be(1);
		report.CasesDegraded.Should().Be(0);
	}

	[Fact]
	public async Task Persona_degrades_recall_when_it_drops_an_expected_chunk()
	{
		// Persona reranking pushes ChunkB out of the top-k window
		// (k=10 here -- so this only triggers when persona literally
		// returns fewer chunks). Using a smaller k makes the test
		// stable: with k=2, neutral returns A,B in top-2; persona
		// returns A,C in top-2 -> B falls outside the window.
		var runner = new PersonaDeltaRunner(new EvalRunnerOptions { RecallK = 2 });

		var report = await runner.RunAsync(
			[Case("c1", ChunkA, ChunkB)],
			neutralBackend: StubRanking(ChunkA, ChunkB, ChunkC),
			personaBackend: StubRanking(ChunkA, ChunkC, ChunkB),
			"engineering");

		report.NeutralRecallAtKAverage.Should().Be(1.0);
		report.PersonaRecallAtKAverage.Should().Be(0.5);
		report.RecallDeltaAverage.Should().Be(-0.5);
		report.CasesDegraded.Should().Be(1);
		report.CasesImproved.Should().Be(0);
	}

	[Fact]
	public async Task Top_one_change_is_tracked_independently_of_recall()
	{
		// Both rankings have ChunkA and ChunkB in the top-10 (so recall
		// is the same), but persona swaps which one is at #1.
		var runner = new PersonaDeltaRunner();

		var report = await runner.RunAsync(
			[Case("c1", ChunkA, ChunkB)],
			neutralBackend: StubRanking(ChunkA, ChunkB, ChunkC),
			personaBackend: StubRanking(ChunkB, ChunkA, ChunkC),
			"engineering");

		report.RecallDeltaAverage.Should().Be(0.0);
		report.TopOneChangedCount.Should().Be(1);
		report.Cases[0].NeutralTopChunkId.Should().Be(ChunkA);
		report.Cases[0].PersonaTopChunkId.Should().Be(ChunkB);
		report.Cases[0].TopOneChanged.Should().BeTrue();
	}

	[Fact]
	public async Task Position_improvement_accumulates_across_expected_chunks()
	{
		// Two expected chunks. Neutral: A at 2, B at 4. Persona: A at 0,
		// B at 1. Improvement = (2-0) + (4-1) = 5.
		var runner = new PersonaDeltaRunner();

		var report = await runner.RunAsync(
			[Case("c1", ChunkA, ChunkB)],
			neutralBackend: StubRanking(ChunkC, ChunkD, ChunkA, ChunkC, ChunkB),
			personaBackend: StubRanking(ChunkA, ChunkB, ChunkC, ChunkD),
			"engineering");

		report.Cases[0].PositionImprovement.Should().Be(5);
	}

	[Fact]
	public async Task Mixed_outcomes_split_into_improved_degraded_unchanged_buckets()
	{
		// Case 1: persona improves recall (-> Improved).
		// Case 2: persona degrades recall (-> Degraded).
		// Case 3: persona keeps recall the same (-> Unchanged).
		var runner = new PersonaDeltaRunner(new EvalRunnerOptions { RecallK = 2 });

		var cases = new[]
		{
			Case("c1-improved", ChunkA, ChunkB),
			Case("c2-degraded", ChunkA, ChunkB),
			Case("c3-unchanged", ChunkA),
		};

		// For c1: neutral misses B; persona surfaces it. Improvement.
		// For c2: neutral has both; persona drops B out of top-2. Degradation.
		// For c3: only A expected; both backends have A at top -- unchanged.
		// Using a switch on case id keeps the test legible.
		RetrievalBackend neutral = (c, _) => Task.FromResult(c.Id switch
		{
			"c1-improved" => new EvalCaseOutcome([ChunkA, ChunkC], 0, 0, false, 0),
			"c2-degraded" => new EvalCaseOutcome([ChunkA, ChunkB], 0, 0, false, 0),
			"c3-unchanged" => new EvalCaseOutcome([ChunkA, ChunkB], 0, 0, false, 0),
			_ => throw new InvalidOperationException(c.Id),
		});
		RetrievalBackend persona = (c, _) => Task.FromResult(c.Id switch
		{
			"c1-improved" => new EvalCaseOutcome([ChunkA, ChunkB], 0, 0, false, 0),
			"c2-degraded" => new EvalCaseOutcome([ChunkA, ChunkC], 0, 0, false, 0),
			"c3-unchanged" => new EvalCaseOutcome([ChunkA, ChunkC], 0, 0, false, 0),
			_ => throw new InvalidOperationException(c.Id),
		});

		var report = await runner.RunAsync(cases, neutral, persona, "engineering");

		report.CaseCount.Should().Be(3);
		report.CasesImproved.Should().Be(1);
		report.CasesDegraded.Should().Be(1);
		report.CasesUnchanged.Should().Be(1);
	}

	[Fact]
	public void PositionImprovement_returns_zero_when_chunk_absent_from_one_side()
	{
		// ChunkB is in the persona ranking but not the neutral one.
		// PositionImprovement only counts chunks present in BOTH; this
		// case contributes 0. (The recall delta captures the presence
		// gain separately.)
		var neutral = new[] { ChunkA, ChunkC };
		var persona = new[] { ChunkA, ChunkB };
		var expected = new HashSet<Guid> { ChunkA, ChunkB };

		var improvement = PersonaDeltaMetrics.PositionImprovement(neutral, persona, expected);

		// ChunkA: neutralIndex=0, personaIndex=0 -> 0 contribution.
		// ChunkB: not in neutral -> 0 contribution.
		improvement.Should().Be(0);
	}

	[Fact]
	public void PositionImprovement_sign_is_preserved()
	{
		// Persona pushes ChunkA DOWN (from index 0 to index 2).
		// Improvement should be negative.
		var neutral = new[] { ChunkA, ChunkB, ChunkC };
		var persona = new[] { ChunkB, ChunkC, ChunkA };
		var expected = new HashSet<Guid> { ChunkA };

		var improvement = PersonaDeltaMetrics.PositionImprovement(neutral, persona, expected);

		improvement.Should().Be(-2);
	}

	[Fact]
	public void Empty_expected_set_yields_zero_position_improvement()
	{
		var improvement = PersonaDeltaMetrics.PositionImprovement(
			new[] { ChunkA, ChunkB },
			new[] { ChunkB, ChunkA },
			new HashSet<Guid>());
		improvement.Should().Be(0);
	}
}
