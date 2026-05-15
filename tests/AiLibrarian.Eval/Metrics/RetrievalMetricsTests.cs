namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Unit tests pinning the math of <see cref="RetrievalMetrics"/>.
/// These run on every PR and protect the CI quality-gate's
/// retrieval thresholds from accidental rounding/algorithm drift.
/// </summary>
public sealed class RetrievalMetricsTests
{
	private static readonly Guid A = new("11111111-1111-1111-1111-111111111111");
	private static readonly Guid B = new("22222222-2222-2222-2222-222222222222");
	private static readonly Guid C = new("33333333-3333-3333-3333-333333333333");
	private static readonly Guid D = new("44444444-4444-4444-4444-444444444444");
	private static readonly Guid E = new("55555555-5555-5555-5555-555555555555");

	[Fact]
	public void RecallAtK_returns_one_when_relevant_set_empty()
	{
		var got = RetrievalMetrics.RecallAtK(
			new[] { A, B, C },
			new HashSet<Guid>(),
			k: 3);

		got.Should().Be(1.0);
	}

	[Fact]
	public void RecallAtK_full_recall_when_all_relevant_in_top_k()
	{
		var got = RetrievalMetrics.RecallAtK(
			new[] { A, B, C, D, E },
			new HashSet<Guid> { A, B },
			k: 3);

		got.Should().Be(1.0);
	}

	[Fact]
	public void RecallAtK_partial_when_some_relevant_outside_top_k()
	{
		// Relevant: {A, B, D}; top-2 retrieved: {A, C} → recall = 1/3
		var got = RetrievalMetrics.RecallAtK(
			new[] { A, C, B, E, D },
			new HashSet<Guid> { A, B, D },
			k: 2);

		got.Should().BeApproximately(1.0 / 3.0, 1e-9);
	}

	[Fact]
	public void RecallAtK_clamps_when_k_exceeds_retrieved_count()
	{
		var got = RetrievalMetrics.RecallAtK(
			new[] { A, B },
			new HashSet<Guid> { A, B, C },
			k: 100);

		got.Should().BeApproximately(2.0 / 3.0, 1e-9);
	}

	[Fact]
	public void RecallAtK_throws_on_zero_k()
	{
		Action act = () => RetrievalMetrics.RecallAtK(
			new[] { A },
			new HashSet<Guid> { A },
			k: 0);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void ReciprocalRank_first_position_is_one()
	{
		var got = RetrievalMetrics.ReciprocalRank(
			new[] { A, B, C },
			new HashSet<Guid> { A });

		got.Should().Be(1.0);
	}

	[Fact]
	public void ReciprocalRank_third_position_is_one_third()
	{
		var got = RetrievalMetrics.ReciprocalRank(
			new[] { A, B, C, D },
			new HashSet<Guid> { C });

		got.Should().BeApproximately(1.0 / 3.0, 1e-9);
	}

	[Fact]
	public void ReciprocalRank_no_hit_returns_zero()
	{
		var got = RetrievalMetrics.ReciprocalRank(
			new[] { A, B, C },
			new HashSet<Guid> { D });

		got.Should().Be(0.0);
	}

	[Fact]
	public void ReciprocalRank_empty_relevant_returns_one()
	{
		var got = RetrievalMetrics.ReciprocalRank(
			new[] { A, B },
			new HashSet<Guid>());

		got.Should().Be(1.0);
	}

	[Fact]
	public void NDcgAtK_perfect_ranking_is_one()
	{
		var grades = new Dictionary<Guid, double> { [A] = 3, [B] = 2, [C] = 1 };

		var got = RetrievalMetrics.NDcgAtK(
			new[] { A, B, C },
			grades,
			k: 3);

		got.Should().BeApproximately(1.0, 1e-9);
	}

	[Fact]
	public void NDcgAtK_reverse_ranking_is_less_than_one()
	{
		var grades = new Dictionary<Guid, double> { [A] = 3, [B] = 2, [C] = 1 };

		var got = RetrievalMetrics.NDcgAtK(
			new[] { C, B, A },
			grades,
			k: 3);

		got.Should().BeLessThan(1.0).And.BeGreaterThan(0.0);
	}

	[Fact]
	public void NDcgAtK_irrelevant_results_score_zero()
	{
		var grades = new Dictionary<Guid, double> { [A] = 3, [B] = 2 };

		var got = RetrievalMetrics.NDcgAtK(
			new[] { C, D, E },
			grades,
			k: 3);

		got.Should().Be(0.0);
	}

	[Fact]
	public void NDcgAtK_empty_grades_returns_one()
	{
		var got = RetrievalMetrics.NDcgAtK(
			new[] { A, B },
			new Dictionary<Guid, double>(),
			k: 2);

		got.Should().Be(1.0);
	}

	[Fact]
	public void NDcgAtK_throws_on_zero_k()
	{
		Action act = () => RetrievalMetrics.NDcgAtK(
			new[] { A },
			new Dictionary<Guid, double> { [A] = 1 },
			k: 0);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}
}
