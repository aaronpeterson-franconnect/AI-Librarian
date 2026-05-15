namespace AiLibrarian.Domain.Citations;

/// <summary>
/// Bulk read of <c>source_chunks.content_markdown</c> for a set of
/// chunk ids. Separate from <see cref="IChunkLookup"/>, which returns
/// metadata-only <see cref="ChunkRef"/> records — this one returns
/// the actual canonical content the LLM needs to synthesize against.
///
/// <para>The Phase 1 hybrid-search return shape truncates content to
/// 600 chars via <c>left(content_markdown, 600) AS excerpt</c>. That
/// keeps retrieval cheap but means downstream LLM consumers (the Wiki
/// Maintainer, future synthesis tools) only see a slice. This reader
/// closes that gap: callers can do one round-trip after retrieval to
/// pull the full canonical text for the chunks they want to cite.</para>
///
/// <para>RLS-respecting: missing-from-result is the
/// "RLS-hidden or hard-deleted" signal. Soft-deleted sources still
/// return rows (the chunk_markdown column survives a source-level
/// soft delete; the soft-delete bit lives on
/// <c>sources.soft_deleted_at</c>, which the validator's
/// <see cref="IChunkLookup"/> consults separately).</para>
/// </summary>
public interface IChunkContentReader
{
	/// <summary>
	/// Return <c>(chunk_id -> content_markdown)</c> for the supplied
	/// ids. Chunks the caller can't see (RLS) are omitted from the
	/// result rather than throwing.
	/// </summary>
	/// <param name="chunkIds">Distinct chunk ids to fetch.</param>
	/// <param name="maxCharsPerChunk">
	/// Cap on the returned text per chunk. Default 4096 — bounded so
	/// the LLM's context window can't blow up on one rogue chunk.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<IReadOnlyDictionary<Guid, string>> ReadContentAsync(
		IReadOnlyCollection<Guid> chunkIds,
		int maxCharsPerChunk = 4096,
		CancellationToken cancellationToken = default);
}
