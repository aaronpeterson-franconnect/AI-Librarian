using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Pins the QualityMetrics aggregator's arithmetic. The actual grader
/// calibration (judge-vs-human inter-rater reliability) is a live-LLM
/// concern that the nightly eval workflow measures; these tests
/// verify the bookkeeping.
/// </summary>
public sealed class QualityMetricsTests
{
	[Fact]
	public void Empty_Input_Returns_Zero_Aggregate()
	{
		var agg = QualityMetrics.Aggregate(Array.Empty<ClaimGrade>());

		agg.Total.Should().Be(0);
		agg.SupportedShare.Should().Be(0.0);
	}

	[Fact]
	public void Counts_Each_Verdict_Independently()
	{
		var grades = new[]
		{
			new ClaimGrade(Guid.NewGuid(), ClaimVerdict.Supported, 0.9, ""),
			new ClaimGrade(Guid.NewGuid(), ClaimVerdict.Supported, 0.9, ""),
			new ClaimGrade(Guid.NewGuid(), ClaimVerdict.NotSupported, 0.8, ""),
			new ClaimGrade(Guid.NewGuid(), ClaimVerdict.Partial, 0.6, ""),
			new ClaimGrade(Guid.NewGuid(), ClaimVerdict.Unverifiable, 0.0, ""),
		};

		var agg = QualityMetrics.Aggregate(grades);

		agg.Total.Should().Be(5);
		agg.Supported.Should().Be(2);
		agg.NotSupported.Should().Be(1);
		agg.Partial.Should().Be(1);
		agg.Unverifiable.Should().Be(1);
		agg.SupportedShare.Should().BeApproximately(0.4, 0.001);
		agg.NotSupportedShare.Should().BeApproximately(0.2, 0.001);
	}

	[Fact]
	public void Shares_Sum_To_One()
	{
		var grades = Enumerable.Range(0, 10)
			.Select(i => new ClaimGrade(Guid.NewGuid(), (ClaimVerdict)(i % 4), 0.5, ""))
			.ToArray();

		var agg = QualityMetrics.Aggregate(grades);

		(agg.SupportedShare + agg.NotSupportedShare + agg.PartialShare + agg.UnverifiableShare)
			.Should().BeApproximately(1.0, 0.001);
	}
}
