namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Unit tests pinning the math of <see cref="SynthesisMetrics"/>.
/// These pin the CI quality-gate thresholds (citation coverage ≥ 95%,
/// refusal rate ≥ 90%) against accidental algorithm drift.
/// </summary>
public sealed class SynthesisMetricsTests
{
	[Theory]
	[InlineData(10, 10, 1.0)]
	[InlineData(10, 9, 0.9)]
	[InlineData(10, 0, 0.0)]
	[InlineData(0, 0, 1.0)] // no claims = trivially covered
	public void CitationCoverage_matches_fraction(int total, int cited, double expected)
	{
		SynthesisMetrics.CitationCoverage(total, cited).Should().BeApproximately(expected, 1e-9);
	}

	[Fact]
	public void CitationCoverage_throws_when_cited_exceeds_total()
	{
		Action act = () => SynthesisMetrics.CitationCoverage(5, 6);
		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Theory]
	[InlineData(20, 20, 1.0)]
	[InlineData(20, 18, 0.9)]
	[InlineData(20, 10, 0.5)]
	[InlineData(0, 0, 1.0)] // no must-refuse cases = trivially passing
	public void RefusalRate_matches_fraction(int must, int refused, double expected)
	{
		SynthesisMetrics.RefusalRate(must, refused).Should().BeApproximately(expected, 1e-9);
	}

	[Fact]
	public void TokensPerCase_zero_cases_returns_zero()
	{
		SynthesisMetrics.TokensPerCase(1234, 0).Should().Be(0.0);
	}

	[Fact]
	public void TokensPerCase_normalizes_total_by_count()
	{
		SynthesisMetrics.TokensPerCase(2000, 4).Should().Be(500.0);
	}
}
