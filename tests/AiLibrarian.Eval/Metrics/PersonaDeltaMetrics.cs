namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Per-case persona delta primitives.  Lives next to
/// <see cref="RetrievalMetrics"/> so callers compose recall + position
/// shifts from one place.
/// </summary>
public static class PersonaDeltaMetrics
{
	/// <summary>
	/// Sum of position improvements for the expected chunks. For each
	/// expected chunk that appears in BOTH rankings, the contribution is
	/// <c>neutralIndex - personaIndex</c> (positive when persona ranks
	/// it higher). Chunks absent from one ranking and present in the
	/// other contribute <c>0</c> to this metric — that signal lives in
	/// the recall delta instead, where presence/absence already counts.
	///
	/// <para>The metric is integer-valued for legibility: "the expected
	/// chunks collectively moved up N slots under persona" reads
	/// straight off the number. Sign-preserving: a negative result
	/// means persona pushed the expected chunks down.</para>
	/// </summary>
	public static int PositionImprovement(
		IReadOnlyList<Guid> neutralRanking,
		IReadOnlyList<Guid> personaRanking,
		IReadOnlySet<Guid> expectedChunks)
	{
		ArgumentNullException.ThrowIfNull(neutralRanking);
		ArgumentNullException.ThrowIfNull(personaRanking);
		ArgumentNullException.ThrowIfNull(expectedChunks);

		if (expectedChunks.Count == 0)
		{
			return 0;
		}

		var neutralIndex = BuildIndex(neutralRanking);
		var personaIndex = BuildIndex(personaRanking);

		var total = 0;
		foreach (var chunk in expectedChunks)
		{
			if (neutralIndex.TryGetValue(chunk, out var nPos)
				&& personaIndex.TryGetValue(chunk, out var pPos))
			{
				total += nPos - pPos;
			}
		}
		return total;
	}

	private static Dictionary<Guid, int> BuildIndex(IReadOnlyList<Guid> ranking)
	{
		var idx = new Dictionary<Guid, int>(capacity: ranking.Count);
		for (var i = 0; i < ranking.Count; i++)
		{
			// First-occurrence wins on duplicates; rankings shouldn't
			// have duplicates but be defensive.
			if (!idx.ContainsKey(ranking[i]))
			{
				idx[ranking[i]] = i;
			}
		}
		return idx;
	}
}
