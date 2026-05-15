namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Pure retrieval-quality metrics. No I/O — callers pass the
/// retrieved chunk-id list (in rank order) and the per-case relevance
/// expectations from <see cref="GoldenCase"/>. Metrics are reported as
/// fractions in [0, 1].
///
/// <para>
/// Hard CI fail thresholds (per the Phase 0 hardening plan):
/// recall@10 regression &gt; 5% absolute; citation coverage &lt; 95%;
/// refusal-when-no-source &lt; 90%. These are enforced by the
/// <c>quality-gate</c> workflow, not by this class.
/// </para>
/// </summary>
public static class RetrievalMetrics
{
	/// <summary>
	/// Recall@k: the fraction of relevant items that appear in the top
	/// <paramref name="k"/> retrieved hits. Returns 1.0 when the
	/// relevant set is empty (no expectation = trivially satisfied).
	/// </summary>
	/// <param name="retrievedInRank">Retrieved chunk IDs, in rank order.</param>
	/// <param name="relevant">Set of chunk IDs that should be retrieved.</param>
	/// <param name="k">Cutoff (top-k); clamped to <c>[1, retrievedInRank.Count]</c>.</param>
	public static double RecallAtK(
		IReadOnlyList<Guid> retrievedInRank,
		IReadOnlySet<Guid> relevant,
		int k)
	{
		ArgumentNullException.ThrowIfNull(retrievedInRank);
		ArgumentNullException.ThrowIfNull(relevant);
		if (k <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
		}

		if (relevant.Count == 0)
		{
			return 1.0;
		}

		var bound = Math.Min(k, retrievedInRank.Count);
		var hits = 0;
		for (var i = 0; i < bound; i++)
		{
			if (relevant.Contains(retrievedInRank[i]))
			{
				hits++;
			}
		}

		return (double)hits / relevant.Count;
	}

	/// <summary>
	/// Mean reciprocal rank of the FIRST relevant hit. Single-query
	/// MRR is reciprocal rank — the harness aggregates across queries
	/// to compute MRR. Returns 0 when no relevant hit appears.
	/// </summary>
	public static double ReciprocalRank(
		IReadOnlyList<Guid> retrievedInRank,
		IReadOnlySet<Guid> relevant)
	{
		ArgumentNullException.ThrowIfNull(retrievedInRank);
		ArgumentNullException.ThrowIfNull(relevant);

		if (relevant.Count == 0)
		{
			return 1.0;
		}

		for (var i = 0; i < retrievedInRank.Count; i++)
		{
			if (relevant.Contains(retrievedInRank[i]))
			{
				return 1.0 / (i + 1);
			}
		}

		return 0.0;
	}

	/// <summary>
	/// Normalized DCG at <paramref name="k"/>. Uses the standard
	/// gain = relevance, discount = log2(rank + 1) formulation. Returns
	/// 1.0 when no graded items are provided (trivially perfect).
	/// </summary>
	/// <param name="retrievedInRank">Retrieved chunk IDs, in rank order.</param>
	/// <param name="relevanceGrades">Map chunk-id → relevance grade
	/// (0 = irrelevant, 3 = highly relevant).</param>
	/// <param name="k">Cutoff (top-k).</param>
	public static double NDcgAtK(
		IReadOnlyList<Guid> retrievedInRank,
		IReadOnlyDictionary<Guid, double> relevanceGrades,
		int k)
	{
		ArgumentNullException.ThrowIfNull(retrievedInRank);
		ArgumentNullException.ThrowIfNull(relevanceGrades);
		if (k <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
		}

		if (relevanceGrades.Count == 0)
		{
			return 1.0;
		}

		var bound = Math.Min(k, retrievedInRank.Count);

		var dcg = 0.0;
		for (var i = 0; i < bound; i++)
		{
			if (relevanceGrades.TryGetValue(retrievedInRank[i], out var grade) && grade > 0)
			{
				dcg += grade / Math.Log2(i + 2);
			}
		}

		// Ideal DCG: place the highest grades in the top positions.
		var ideal = 0.0;
		var sortedGrades = relevanceGrades.Values.Where(g => g > 0).OrderByDescending(g => g).ToList();
		var idealBound = Math.Min(k, sortedGrades.Count);
		for (var i = 0; i < idealBound; i++)
		{
			ideal += sortedGrades[i] / Math.Log2(i + 2);
		}

		return ideal == 0 ? 1.0 : dcg / ideal;
	}
}
