namespace AiLibrarian.Infrastructure.Retrieval;

/// <summary>
/// One ranked chunk from hybrid search.
///
/// <para><see cref="SourceCreatedAt"/> and <see cref="SourceApprovedAt"/>
/// were added with persona-aware retrieval (ADR 0015) so the persona
/// reranker can compute recency decay and authority bias from the
/// hit alone. They are nullable so older callers / pre-Phase-1
/// fixtures that don't populate them get no-op rerank behavior
/// rather than NREs.</para>
/// </summary>
public sealed record HybridChunkHit(
	Guid ChunkId,
	Guid SourceId,
	int OrderIndex,
	string Excerpt,
	double HybridScore,
	double? CosineDistance,
	double? TextRank,
	AiLibrarian.Domain.Classification SourceClassification = AiLibrarian.Domain.Classification.Internal,
	Guid SourceDepartmentId = default,
	DateTimeOffset? SourceCreatedAt = null,
	DateTimeOffset? SourceApprovedAt = null,
	string? SourceType = null);
