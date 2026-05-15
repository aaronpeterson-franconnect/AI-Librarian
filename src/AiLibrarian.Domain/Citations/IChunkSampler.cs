namespace AiLibrarian.Domain.Citations;

/// <summary>
/// Pull a representative sample of source chunks for a department, with
/// no query/topic constraint. Used by the candidate-page discovery flow
/// where we don't yet know what topics exist — the sampler returns
/// chunks so the discovery pipeline can embed + cluster them to surface
/// topic candidates.
///
/// <para>Distinct from <see cref="IChunkContentReader"/>: that reader
/// is bulk-by-id (caller knows which chunks). The sampler is
/// bulk-by-department with random sampling so a 10k-chunk corpus
/// doesn't over-weight any one source.</para>
///
/// <para>Implementations must respect <c>sources.soft_deleted_at</c>
/// (skip deleted sources) and may bound content per chunk so a huge
/// chunk can't dominate the sample.</para>
/// </summary>
public interface IChunkSampler
{
	/// <summary>
	/// Return up to <paramref name="limit"/> chunks for the given
	/// department. Random sample (not recency-ordered) so clustering
	/// sees the whole corpus rather than just newest content.
	/// </summary>
	/// <param name="departmentId">Owning department.</param>
	/// <param name="limit">Max chunks to return. Implementations typically clamp to a reasonable upper bound.</param>
	/// <param name="maxCharsPerChunk">Per-chunk content cap (server-side truncation).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<IReadOnlyList<SampledChunk>> SampleAsync(
		Guid departmentId,
		int limit,
		int maxCharsPerChunk = 4096,
		CancellationToken cancellationToken = default);
}

/// <summary>One chunk returned by <see cref="IChunkSampler"/>.</summary>
/// <param name="ChunkId">Stable chunk identifier.</param>
/// <param name="ContentMarkdown">Canonical chunk content (capped by <c>maxCharsPerChunk</c>).</param>
/// <param name="Classification">Owning source's classification — propagated so cluster naming can sanity-check whether a candidate should land on a Public, Internal, or higher facet.</param>
public sealed record SampledChunk(
	Guid ChunkId,
	string ContentMarkdown,
	Classification Classification);
