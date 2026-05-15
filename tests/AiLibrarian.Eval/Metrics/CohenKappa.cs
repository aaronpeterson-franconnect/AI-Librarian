using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Cohen's κ — agreement between two raters on a categorical scale,
/// chance-corrected. Used as the eval harness's LLM-judge-vs-human
/// calibration metric per the hardening plan:
///
/// <list type="bullet">
///   <item>κ &gt; 0.8 — near-perfect agreement.</item>
///   <item>κ &gt; 0.7 — substantial agreement; CI warn threshold.</item>
///   <item>κ &gt; 0.6 — moderate; investigate the judge prompt.</item>
///   <item>κ &lt; 0.4 — poor; the calibration set or prompt needs work.</item>
/// </list>
///
/// <para>Formula:
/// <c>κ = (p_o - p_e) / (1 - p_e)</c> where <c>p_o</c> is the observed
/// proportion of agreement and <c>p_e</c> is the expected proportion of
/// agreement by chance, computed from each rater's marginals.</para>
///
/// <para>Edge cases:
/// <list type="bullet">
///   <item>Empty input → 0.0 (no information).</item>
///   <item>Both raters perfectly agree → 1.0, regardless of marginals.</item>
///   <item>Both raters use only one class identically → 1.0 (κ degenerates; we treat it as agreement rather than NaN to keep the CI gate well-defined).</item>
///   <item>Lists with mismatched lengths → <see cref="ArgumentException"/>.</item>
/// </list>
/// </para>
/// </summary>
public static class CohenKappa
{
	/// <summary>
	/// Cohen's κ between two raters over the four
	/// <see cref="ClaimVerdict"/> classes. Lists must be the same length;
	/// the i-th element of <paramref name="raterA"/> is paired with the
	/// i-th element of <paramref name="raterB"/>.
	/// </summary>
	public static double Compute(
		IReadOnlyList<ClaimVerdict> raterA,
		IReadOnlyList<ClaimVerdict> raterB)
	{
		ArgumentNullException.ThrowIfNull(raterA);
		ArgumentNullException.ThrowIfNull(raterB);

		var n = raterA.Count;
		if (n != raterB.Count)
		{
			throw new ArgumentException(
				$"Rater lists must be the same length. raterA={n}, raterB={raterB.Count}.",
				nameof(raterB));
		}

		if (n == 0)
		{
			return 0.0;
		}

		// 4 classes -- Supported, NotSupported, Partial, Unverifiable.
		const int numClasses = 4;
		var marginalA = new int[numClasses];
		var marginalB = new int[numClasses];
		var agreed = 0;

		for (var i = 0; i < n; i++)
		{
			var a = (int)raterA[i];
			var b = (int)raterB[i];
			marginalA[a]++;
			marginalB[b]++;
			if (a == b)
			{
				agreed++;
			}
		}

		var pO = (double)agreed / n;
		var pE = 0.0;
		for (var c = 0; c < numClasses; c++)
		{
			pE += ((double)marginalA[c] / n) * ((double)marginalB[c] / n);
		}

		// Degenerate: both raters used exactly one class and agreed
		// throughout (p_e = 1, p_o = 1). The formula divides by zero;
		// return 1.0 since the agreement is total.
		if (pE >= 1.0)
		{
			return pO >= 1.0 ? 1.0 : 0.0;
		}

		return (pO - pE) / (1.0 - pE);
	}

	/// <summary>
	/// Confusion-matrix view of a rater pair. Index <c>[a, b]</c> = the
	/// number of cases where rater A picked verdict <c>a</c> and rater
	/// B picked verdict <c>b</c>. Used for the eval report's
	/// "where do they disagree?" diagnostic block.
	/// </summary>
	public static int[,] ConfusionMatrix(
		IReadOnlyList<ClaimVerdict> raterA,
		IReadOnlyList<ClaimVerdict> raterB)
	{
		ArgumentNullException.ThrowIfNull(raterA);
		ArgumentNullException.ThrowIfNull(raterB);

		if (raterA.Count != raterB.Count)
		{
			throw new ArgumentException(
				"Rater lists must be the same length.",
				nameof(raterB));
		}

		const int numClasses = 4;
		var matrix = new int[numClasses, numClasses];
		for (var i = 0; i < raterA.Count; i++)
		{
			matrix[(int)raterA[i], (int)raterB[i]]++;
		}
		return matrix;
	}
}
