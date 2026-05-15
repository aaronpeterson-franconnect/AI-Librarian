using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Infrastructure.Retrieval;

/// <summary>Used when no Postgres connection string is configured.</summary>
public sealed class NullHybridChunkSearch : IHybridChunkSearch
{
	public Task<IReadOnlyList<HybridChunkHit>> SearchAsync(
		RlsSessionContext sessionContext,
		string queryText,
		ReadOnlyMemory<float> queryEmbedding,
		HybridSearchRequestOptions options,
		CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<HybridChunkHit>>(Array.Empty<HybridChunkHit>());
}
