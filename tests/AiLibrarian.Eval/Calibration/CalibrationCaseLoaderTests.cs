using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Eval.Calibration;

/// <summary>
/// Pins the calibration-case loader against the starter set of 20
/// YAMLs that live under
/// <c>tests/AiLibrarian.Eval/golden-sets/calibration/</c>. Authors who
/// add or remove cases will see these assertions fail until the
/// counts are updated — that's the intended forcing function so the
/// rubric's 20-case discipline doesn't drift silently.
/// </summary>
public sealed class CalibrationCaseLoaderTests
{
	private static readonly string CalibrationDir = Path.Combine(
		AppContext.BaseDirectory,
		"golden-sets",
		"calibration");

	[Fact]
	public void Starter_set_has_exactly_20_cases()
	{
		var cases = CalibrationCaseLoader.LoadAll(CalibrationDir);
		cases.Should().HaveCount(20, "the rubric specifies a starter floor of 20 cases");
	}

	[Fact]
	public void Starter_set_covers_all_four_verdicts()
	{
		var cases = CalibrationCaseLoader.LoadAll(CalibrationDir);
		var verdicts = cases.Select(c => c.HumanVerdict).Distinct().ToHashSet();

		verdicts.Should().Contain(ClaimVerdict.Supported);
		verdicts.Should().Contain(ClaimVerdict.NotSupported);
		verdicts.Should().Contain(ClaimVerdict.Partial);
		verdicts.Should().Contain(ClaimVerdict.Unverifiable);
	}

	[Fact]
	public void Starter_set_verdict_mix_matches_the_rubric()
	{
		var cases = CalibrationCaseLoader.LoadAll(CalibrationDir);
		var counts = cases
			.GroupBy(c => c.HumanVerdict)
			.ToDictionary(g => g.Key, g => g.Count());

		// 8 / 5 / 4 / 3 per the calibration README.
		counts[ClaimVerdict.Supported].Should().Be(8);
		counts[ClaimVerdict.NotSupported].Should().Be(5);
		counts[ClaimVerdict.Partial].Should().Be(4);
		counts[ClaimVerdict.Unverifiable].Should().Be(3);
	}

	[Fact]
	public void Every_case_has_at_least_one_cited_chunk()
	{
		var cases = CalibrationCaseLoader.LoadAll(CalibrationDir);
		cases.Should().AllSatisfy(c =>
			c.CitedChunks.Should().NotBeEmpty(
				$"case {c.Id} must cite at least one chunk; otherwise the grader has nothing to score against"));
	}

	[Fact]
	public void Every_case_has_a_non_empty_claim_text()
	{
		var cases = CalibrationCaseLoader.LoadAll(CalibrationDir);
		cases.Should().AllSatisfy(c =>
			c.ClaimText.Should().NotBeNullOrWhiteSpace($"case {c.Id} needs a real claim_text"));
	}

	[Fact]
	public void LoadAll_returns_empty_for_missing_directory()
	{
		var cases = CalibrationCaseLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "no-such-dir"));
		cases.Should().BeEmpty();
	}
}
