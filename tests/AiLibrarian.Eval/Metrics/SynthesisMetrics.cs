namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Pure synthesis-quality metrics derived from a synthesized answer
/// plus its claim/citation expectations. The LLM-as-judge grader
/// (citation precision via spot-check) ships in week 3 of the Phase 0
/// hardening plan with the citation validator; this class covers the
/// non-judge metrics that depend only on structural properties.
/// </summary>
public static class SynthesisMetrics
{
	/// <summary>
	/// Citation coverage: the fraction of synthesized claims that
	/// carry at least one citation. The hardening plan's CI gate
	/// requires this stays at or above 95% in aggregate.
	/// </summary>
	/// <param name="totalClaims">Number of claims in the synthesized answer.</param>
	/// <param name="citedClaims">Number of those claims with at least one citation.</param>
	public static double CitationCoverage(int totalClaims, int citedClaims)
	{
		if (totalClaims < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(totalClaims), totalClaims, "Must be non-negative.");
		}

		if (citedClaims < 0 || citedClaims > totalClaims)
		{
			throw new ArgumentOutOfRangeException(nameof(citedClaims), citedClaims, "Must be in [0, totalClaims].");
		}

		return totalClaims == 0 ? 1.0 : (double)citedClaims / totalClaims;
	}

	/// <summary>
	/// Refusal-when-no-source rate: of the must-refuse golden cases,
	/// the fraction where the system actually refused (returned a
	/// no-answer / "I don't have evidence" response). Hardening-plan
	/// gate: ≥ 90%.
	/// </summary>
	/// <param name="mustRefuseCases">Total must-refuse cases evaluated.</param>
	/// <param name="actuallyRefused">Cases where the system refused.</param>
	public static double RefusalRate(int mustRefuseCases, int actuallyRefused)
	{
		if (mustRefuseCases < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(mustRefuseCases), mustRefuseCases, "Must be non-negative.");
		}

		if (actuallyRefused < 0 || actuallyRefused > mustRefuseCases)
		{
			throw new ArgumentOutOfRangeException(nameof(actuallyRefused), actuallyRefused, "Must be in [0, mustRefuseCases].");
		}

		return mustRefuseCases == 0 ? 1.0 : (double)actuallyRefused / mustRefuseCases;
	}

	/// <summary>
	/// Token-cost regression signal: total prompt + completion tokens
	/// normalized per evaluated case. CI tracks this for trend
	/// detection; not a hard fail gate today.
	/// </summary>
	public static double TokensPerCase(long totalTokens, int caseCount)
	{
		if (totalTokens < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(totalTokens), totalTokens, "Must be non-negative.");
		}

		if (caseCount < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(caseCount), caseCount, "Must be non-negative.");
		}

		return caseCount == 0 ? 0.0 : (double)totalTokens / caseCount;
	}
}
