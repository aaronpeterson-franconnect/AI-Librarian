using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Pins the κ arithmetic. The actual judge-vs-human calibration is a
/// live-LLM concern measured by the nightly eval workflow; these tests
/// verify the chance-correction math and the edge cases the CI gate
/// depends on staying stable.
/// </summary>
public sealed class CohenKappaTests
{
	[Fact]
	public void Empty_Input_Returns_Zero()
	{
		var k = CohenKappa.Compute(
			Array.Empty<ClaimVerdict>(),
			Array.Empty<ClaimVerdict>());

		k.Should().Be(0.0);
	}

	[Fact]
	public void Perfect_Agreement_Returns_One()
	{
		var a = new[]
		{
			ClaimVerdict.Supported,
			ClaimVerdict.NotSupported,
			ClaimVerdict.Partial,
			ClaimVerdict.Unverifiable,
			ClaimVerdict.Supported,
		};

		var k = CohenKappa.Compute(a, a);

		k.Should().BeApproximately(1.0, 1e-9);
	}

	[Fact]
	public void Total_Disagreement_Returns_Negative()
	{
		// 4 cases, every verdict different between raters; chance
		// agreement is positive but observed is zero -> κ < 0.
		var a = new[]
		{
			ClaimVerdict.Supported,
			ClaimVerdict.NotSupported,
			ClaimVerdict.Partial,
			ClaimVerdict.Unverifiable,
		};
		var b = new[]
		{
			ClaimVerdict.NotSupported,
			ClaimVerdict.Supported,
			ClaimVerdict.Unverifiable,
			ClaimVerdict.Partial,
		};

		var k = CohenKappa.Compute(a, b);

		k.Should().BeLessThan(0.0);
	}

	[Fact]
	public void Independent_Raters_With_Same_Marginals_Return_Approximately_Zero()
	{
		// Both raters pick Supported 50% of the time, NotSupported 50%,
		// but their choices are uncorrelated -> κ should hover near 0.
		var a = new[]
		{
			ClaimVerdict.Supported,    // agree
			ClaimVerdict.NotSupported, // disagree
			ClaimVerdict.Supported,    // disagree
			ClaimVerdict.NotSupported, // agree
		};
		var b = new[]
		{
			ClaimVerdict.Supported,
			ClaimVerdict.Supported,
			ClaimVerdict.NotSupported,
			ClaimVerdict.NotSupported,
		};

		var k = CohenKappa.Compute(a, b);

		// p_o = 0.5, p_e = 0.5 -> κ = 0 exactly.
		k.Should().BeApproximately(0.0, 1e-9);
	}

	[Fact]
	public void Substantial_Agreement_Crosses_The_CI_Warn_Threshold()
	{
		// 9 of 10 agree, balanced marginals -> κ should be well above 0.7.
		var a = new[]
		{
			ClaimVerdict.Supported, ClaimVerdict.Supported, ClaimVerdict.Supported,
			ClaimVerdict.NotSupported, ClaimVerdict.NotSupported, ClaimVerdict.NotSupported,
			ClaimVerdict.Partial, ClaimVerdict.Partial,
			ClaimVerdict.Unverifiable, ClaimVerdict.Unverifiable,
		};
		var b = new[]
		{
			ClaimVerdict.Supported, ClaimVerdict.Supported, ClaimVerdict.Supported,
			ClaimVerdict.NotSupported, ClaimVerdict.NotSupported, ClaimVerdict.NotSupported,
			ClaimVerdict.Partial, ClaimVerdict.Partial,
			ClaimVerdict.Unverifiable, ClaimVerdict.Supported, // one disagreement
		};

		var k = CohenKappa.Compute(a, b);

		k.Should().BeGreaterThan(0.7, "9/10 agreement with balanced marginals should clear the CI warn threshold");
	}

	[Fact]
	public void Both_Raters_Pick_Same_Single_Class_Returns_One()
	{
		// Degenerate: both raters always pick Supported. p_e = 1, p_o = 1
		// -> the formula divides by zero. We return 1.0 (total agreement)
		// rather than NaN to keep the CI gate well-defined.
		var all = Enumerable.Range(0, 5).Select(_ => ClaimVerdict.Supported).ToArray();

		var k = CohenKappa.Compute(all, all);

		k.Should().Be(1.0);
	}

	[Fact]
	public void Mismatched_Length_Throws()
	{
		var a = new[] { ClaimVerdict.Supported };
		var b = new[] { ClaimVerdict.Supported, ClaimVerdict.NotSupported };

		var act = () => CohenKappa.Compute(a, b);
		act.Should().Throw<ArgumentException>().WithMessage("*length*");
	}

	[Fact]
	public void ConfusionMatrix_Counts_Each_Pair()
	{
		var a = new[]
		{
			ClaimVerdict.Supported,
			ClaimVerdict.Supported,
			ClaimVerdict.Partial,
			ClaimVerdict.NotSupported,
		};
		var b = new[]
		{
			ClaimVerdict.Supported,
			ClaimVerdict.Partial,         // disagreement: human Supported, judge Partial
			ClaimVerdict.Partial,
			ClaimVerdict.NotSupported,
		};

		var m = CohenKappa.ConfusionMatrix(a, b);

		m[(int)ClaimVerdict.Supported, (int)ClaimVerdict.Supported].Should().Be(1);
		m[(int)ClaimVerdict.Supported, (int)ClaimVerdict.Partial].Should().Be(1);
		m[(int)ClaimVerdict.Partial, (int)ClaimVerdict.Partial].Should().Be(1);
		m[(int)ClaimVerdict.NotSupported, (int)ClaimVerdict.NotSupported].Should().Be(1);
	}
}
