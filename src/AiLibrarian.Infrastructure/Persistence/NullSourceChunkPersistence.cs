using AiLibrarian.Domain.Skills;

using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>No-op when Postgres is not configured (local dev).</summary>
public sealed class NullSourceChunkPersistence : ISourceChunkPersistence
{
	public Task ReplaceChunksForSourceAsync(
		RlsSessionContext sessionContext,
		Guid sourceId,
		IReadOnlyList<Chunk> chunks,
		CancellationToken cancellationToken)
		=> Task.CompletedTask;

	public Task UpdateEmbeddingsForSourceAsync(
		RlsSessionContext sessionContext,
		Guid sourceId,
		string embeddingModel,
		IReadOnlyList<(int OrderIndex, ReadOnlyMemory<float> Embedding)> vectors,
		CancellationToken cancellationToken)
		=> Task.CompletedTask;
}
