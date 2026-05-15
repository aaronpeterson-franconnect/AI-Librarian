using AiLibrarian.Domain;

namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// Surface the candidate-page discovery flow exposes to the API. Given
/// a department, the generator samples representative chunks, clusters
/// them by semantic similarity, asks the LLM to name each cluster, and
/// returns the candidates as a flat list ready for an operator to
/// review and selectively materialize via <c>/api/admin/wiki/discover</c>.
///
/// <para>v1 deliberately returns candidates only — no review-queue
/// table, no auto-materialize, no UI. The operator eyeballs the list
/// and decides which ones to keep. This keeps the slice bounded while
/// closing the "what pages should I have?" question.</para>
///
/// <para>RLS: the generator runs in system admin context internally
/// (because cross-source clustering needs department-wide visibility
/// regardless of caller membership). The API handler is what enforces
/// "operator may discover within this department" — typically Admin
/// only in v1.</para>
/// </summary>
public interface IWikiPageCandidateGenerator
{
	/// <summary>
	/// Run the discovery pipeline against <paramref name="departmentId"/>.
	/// </summary>
	/// <param name="departmentId">Owning department.</param>
	/// <param name="sampleSize">How many source chunks to sample. Default 100.</param>
	/// <param name="maxCandidates">How many cluster-derived candidates to return. Default 5.</param>
	/// <param name="correlationId">Correlation token threading the LLM calls + audit.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<WikiPageCandidateBatch> DiscoverAsync(
		Guid departmentId,
		int sampleSize,
		int maxCandidates,
		Guid correlationId,
		CancellationToken cancellationToken = default);
}

/// <summary>Result of one candidate-discovery run.</summary>
/// <param name="Candidates">Ordered list of candidates (largest cluster first).</param>
/// <param name="SampledChunkCount">How many chunks were actually sampled (may be below the requested size if the corpus is small).</param>
/// <param name="EmbeddingDeployment">Embedding deployment used for the run; useful for audit stamping.</param>
public sealed record WikiPageCandidateBatch(
	IReadOnlyList<WikiPageCandidate> Candidates,
	int SampledChunkCount,
	string EmbeddingDeployment);

/// <summary>One proposed wiki page candidate from a cluster of chunks.</summary>
/// <param name="ProposedTitle">LLM-suggested human title (≤80 chars).</param>
/// <param name="ProposedSlug">URL-safe slug, validated against <see cref="WikiSlug.IsValid"/>.</param>
/// <param name="Summary">One- or two-sentence cluster summary.</param>
/// <param name="HighestClassification">Highest classification observed across the cluster's chunks — recommended ceiling for the facet.</param>
/// <param name="SupportingChunkIds">Chunks that fed the naming prompt (cluster representatives). Useful for the operator's "show me why" review.</param>
/// <param name="ClusterSize">Total chunks in the cluster.</param>
public sealed record WikiPageCandidate(
	string ProposedTitle,
	string ProposedSlug,
	string Summary,
	Classification HighestClassification,
	IReadOnlyList<Guid> SupportingChunkIds,
	int ClusterSize);
