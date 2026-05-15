using AiLibrarian.LlmGateway.Abstractions;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// Deterministic passthrough reranker used until a real rerank model (e.g.
/// Cohere, Azure AI Search semantic ranker, or cross-encoder) is wired. Preserves
/// candidate order with monotonically decreasing scores so downstream code can
/// treat the list as ranked without fabricating relevance.
/// </summary>
public sealed class NoOpRerankProvider : IRerankProvider
{
	/// <inheritdoc />
	public string ProviderId => "noop";

	/// <inheritdoc />
	public Task<IReadOnlyList<RerankResult>> RerankAsync(
		string model,
		string query,
		IReadOnlyList<RerankCandidate> candidates,
		Guid correlationId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(model);
		ArgumentNullException.ThrowIfNull(query);
		ArgumentNullException.ThrowIfNull(candidates);

		if (candidates.Count == 0)
		{
			return Task.FromResult<IReadOnlyList<RerankResult>>(Array.Empty<RerankResult>());
		}

		var results = new List<RerankResult>(candidates.Count);
		for (var i = 0; i < candidates.Count; i++)
		{
			// Slightly decreasing scores preserve order without claiming calibrated relevance.
			var score = 1.0 - i * 1e-6;
			results.Add(new RerankResult(candidates[i].Key, score));
		}

		return Task.FromResult<IReadOnlyList<RerankResult>>(results);
	}
}
