namespace AiLibrarian.Domain.Citations;

/// <summary>
/// Storage-agnostic lookup the citation validator depends on. Tests
/// supply a dictionary-backed implementation; production wires a
/// Postgres reader behind it. Keeping the interface in Domain means
/// <c>CitationValidator</c> (in <c>AiLibrarian.Quality</c>) can
/// be exercised without any database dependency.
/// </summary>
public interface IChunkLookup
{
	/// <summary>
	/// Resolves the supplied chunk ids in one batch. Missing or
	/// hard-deleted ids must be absent from the returned dictionary;
	/// soft-deleted chunks must be present with
	/// <see cref="ChunkRef.IsSoftDeleted"/> true so rule 2 can flag the
	/// dangling-citation case.
	/// </summary>
	/// <param name="chunkIds">Distinct chunk ids to resolve.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<IReadOnlyDictionary<Guid, ChunkRef>> ResolveAsync(
		IReadOnlyCollection<Guid> chunkIds,
		CancellationToken cancellationToken = default);
}
