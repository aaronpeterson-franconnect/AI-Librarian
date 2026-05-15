using AiLibrarian.Domain.Skills;

using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>Replaces derived chunks for a source row after skill canonicalization.</summary>
public interface ISourceChunkPersistence
{
	Task ReplaceChunksForSourceAsync(
		RlsSessionContext sessionContext,
		Guid sourceId,
		IReadOnlyList<Chunk> chunks,
		CancellationToken cancellationToken);

	/// <summary>Writes embeddings for existing chunk rows (matched by source_id + order_index).</summary>
	Task UpdateEmbeddingsForSourceAsync(
		RlsSessionContext sessionContext,
		Guid sourceId,
		string embeddingModel,
		IReadOnlyList<(int OrderIndex, ReadOnlyMemory<float> Embedding)> vectors,
		CancellationToken cancellationToken);
}
