namespace AiLibrarian.Domain.Citations;

/// <summary>
/// Minimal projection of a <c>source_chunks</c> row that the citation
/// validator needs. Lookup implementations (in-memory for tests,
/// Postgres for prod via <c>AiLibrarian.Infrastructure</c>) return one
/// of these per chunk id; the validator stays storage-agnostic.
/// </summary>
/// <param name="Id">The chunk identifier.</param>
/// <param name="SourceId">The owning source (for cross-checking).</param>
/// <param name="Classification">
/// The chunk's effective classification. Inherited from the source unless
/// the chunk carries its own override; rule 4 compares this against the
/// facet ceiling.
/// </param>
/// <param name="ContentLength">
/// Character length of the canonicalized chunk text. Rule 3 requires
/// citation spans to lie within <c>[0, ContentLength]</c>.
/// </param>
/// <param name="IsSoftDeleted">
/// True when the chunk's owning source has a non-null
/// <c>soft_deleted_at</c>. Rule 2 fails the citation in that case —
/// the chunk is unreachable through RLS.
/// </param>
public sealed record ChunkRef(
	Guid Id,
	Guid SourceId,
	Classification Classification,
	int ContentLength,
	bool IsSoftDeleted);
