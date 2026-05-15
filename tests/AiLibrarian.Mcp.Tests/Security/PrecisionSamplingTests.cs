using AiLibrarian.Security;

namespace AiLibrarian.Mcp.Tests.Security;

/// <summary>
/// Pins the precision-sampling math + enforce-readiness verdict. The
/// rules from ADR 0017:
/// <list type="bullet">
///   <item>Overall precision ≥ floor.</item>
///   <item>Every kind that fired ≥ floor.</item>
///   <item>Total labeled samples ≥ minSampleSize (default 100).</item>
/// </list>
/// </summary>
public sealed class PrecisionSamplingTests
{
	[Fact]
	public void Empty_Labels_Returns_NotReady_With_Sample_Size_Reason()
	{
		var report = PrecisionSampling.Compute(Array.Empty<LabeledCandidate>());

		report.TotalLabeled.Should().Be(0);
		report.EnforceReady.Should().BeFalse();
		report.Reasons.Should().Contain(r => r.Contains("Sample size"));
	}

	[Fact]
	public void All_True_Positives_With_Adequate_Sample_Is_Ready()
	{
		var labels = Enumerable.Range(0, 110)
			.Select(_ => new LabeledCandidate("jwt", true))
			.ToArray();

		var report = PrecisionSampling.Compute(labels);

		report.OverallPrecision.Should().Be(1.0);
		report.EnforceReady.Should().BeTrue();
		report.Reasons.Should().Contain(r => r.Contains("All gates met"));
	}

	[Fact]
	public void One_Kind_Below_Floor_Blocks_Enforce()
	{
		// jwt: 90% precision (good); api_key_assignment: 70% (bad).
		// Overall: 80% — also below floor. We expect the verdict to
		// surface BOTH reasons so the operator sees the actionable
		// fix.
		var labels = new List<LabeledCandidate>();
		for (var i = 0; i < 60; i++)
		{
			labels.Add(new LabeledCandidate("jwt", IsTruePositive: true));
		}

		for (var i = 0; i < 6; i++)
		{
			labels.Add(new LabeledCandidate("jwt", IsTruePositive: false));
		}

		for (var i = 0; i < 35; i++)
		{
			labels.Add(new LabeledCandidate("api_key_assignment", IsTruePositive: true));
		}

		for (var i = 0; i < 15; i++)
		{
			labels.Add(new LabeledCandidate("api_key_assignment", IsTruePositive: false));
		}

		var report = PrecisionSampling.Compute(labels);

		report.EnforceReady.Should().BeFalse();
		report.Reasons.Should().Contain(r => r.Contains("api_key_assignment") && r.Contains("below floor"));

		var jwt = report.PerKind.First(p => p.Kind == "jwt");
		jwt.Precision.Should().BeApproximately(60.0 / 66.0, 0.001);

		var apiKey = report.PerKind.First(p => p.Kind == "api_key_assignment");
		apiKey.Precision.Should().Be(0.7);
	}

	[Fact]
	public void Custom_Floor_Tightens_The_Gate()
	{
		var labels = Enumerable.Range(0, 100)
			.Select(i => new LabeledCandidate("aws_access_key", IsTruePositive: i < 92))
			.ToArray();

		// Overall = 0.92. Default floor 0.9 -> ready; tighter floor 0.95 -> not ready.
		var ready = PrecisionSampling.Compute(labels);
		ready.EnforceReady.Should().BeTrue();

		var stricter = PrecisionSampling.Compute(labels, precisionFloor: 0.95);
		stricter.EnforceReady.Should().BeFalse();
		stricter.Reasons.Should().Contain(r => r.Contains("Overall precision"));
	}

	[Fact]
	public void Kinds_With_Zero_Samples_Are_Noted_But_Not_Blocking()
	{
		// Only one kind has labels; the verdict shouldn't fault us for
		// other kinds we haven't seen yet.
		var labels = Enumerable.Range(0, 110)
			.Select(_ => new LabeledCandidate("github_token", IsTruePositive: true))
			.ToArray();

		var report = PrecisionSampling.Compute(labels);

		report.EnforceReady.Should().BeTrue();
		report.PerKind.Should().ContainSingle(p => p.Kind == "github_token");
	}

	[Fact]
	public void Sample_Size_Floor_Customizable()
	{
		var labels = Enumerable.Range(0, 20)
			.Select(_ => new LabeledCandidate("pem", IsTruePositive: true))
			.ToArray();

		// Default minSampleSize=100 -> not ready (small sample).
		var defaultReport = PrecisionSampling.Compute(labels);
		defaultReport.EnforceReady.Should().BeFalse();

		// Custom minSampleSize=10 -> ready.
		var custom = PrecisionSampling.Compute(labels, minSampleSize: 10);
		custom.EnforceReady.Should().BeTrue();
	}

	[Fact]
	public void Invalid_Floor_Rejected()
	{
		var act = () => PrecisionSampling.Compute(Array.Empty<LabeledCandidate>(), precisionFloor: 1.5);
		act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("precisionFloor");
	}

	[Fact]
	public void Per_Kind_Rows_Are_Stable_Sorted_By_Kind()
	{
		var labels = new[]
		{
			new LabeledCandidate("zebra", true),
			new LabeledCandidate("apple", true),
			new LabeledCandidate("mango", false),
		};

		var report = PrecisionSampling.Compute(labels);
		report.PerKind.Select(p => p.Kind).Should().Equal("apple", "mango", "zebra");
	}
}
