using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Infrastructure.Retrieval;

public interface IHybridChunkSearch
{
	Task<IReadOnlyList<HybridChunkHit>> SearchAsync(
		RlsSessionContext sessionContext,
		string queryText,
		ReadOnlyMemory<float> queryEmbedding,
		HybridSearchRequestOptions options,
		CancellationToken cancellationToken);
}
