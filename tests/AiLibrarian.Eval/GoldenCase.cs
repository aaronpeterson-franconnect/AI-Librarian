using AiLibrarian.Domain;

namespace AiLibrarian.Eval;

/// <summary>
/// One golden-set case for retrieval / synthesis evaluation. Authored
/// in YAML under <c>golden-sets/&lt;persona&gt;/*.yaml</c>; the loader
/// (separate file, week 2 of the hardening plan) deserializes into
/// this record.
///
/// <para>
/// Schema is intentionally minimal: a query, the chunk IDs that
/// should appear among the top-k retrieval hits, the citation
/// expectations on the synthesized answer, and a refusal flag for
/// "must refuse" cases (queries with no supportable answer in the
/// corpus). Persona scopes the run; classification scopes the
/// expected RLS-filtered candidate set.
/// </para>
/// </summary>
public sealed record GoldenCase(
	string Id,
	string Query,
	string Persona,
	Classification ClassificationScope,
	IReadOnlyList<Guid> ExpectedChunkIds,
	IReadOnlyList<ExpectedCitation> ExpectedCitations,
	bool MustRefuse,
	IReadOnlyDictionary<string, string> Tags)
{
	/// <summary>
	/// Map from chunk-id to graded relevance (0 = irrelevant, 3 =
	/// highly relevant). Used by <c>RetrievalMetrics.NDcg</c>. By
	/// default each <see cref="ExpectedChunkIds"/> entry gets
	/// relevance = 1.
	/// </summary>
	public IReadOnlyDictionary<Guid, double> RelevanceGrades(double defaultGrade = 1.0)
	{
		var dict = new Dictionary<Guid, double>(capacity: ExpectedChunkIds.Count);
		foreach (var id in ExpectedChunkIds)
		{
			dict[id] = defaultGrade;
		}

		return dict;
	}
}

/// <summary>
/// One expected citation in a synthesized answer. The citation
/// validator (<c>AiLibrarian.Quality.CitationValidator</c>, landing in
/// week 3 of the Phase 0 hardening plan) consumes this shape; the eval
/// harness compares actual answer citations against this set to derive
/// citation precision and coverage metrics.
/// </summary>
public sealed record ExpectedCitation(
	Guid SourceId,
	string SpanAnchor,
	double MinConfidence);
