namespace AiLibrarian.LlmGateway.Abstractions;

/// <summary>
/// Provider-agnostic embedding contract per ADR 0003. Used by the
/// ingestion pipeline (chunk embedding) and retrieval (query embedding).
/// </summary>
public interface IEmbeddingProvider
{
	/// <summary>The provider this implementation represents.</summary>
	string ProviderId { get; }

	/// <summary>
	/// Embed a batch of inputs in one call. Implementations must emit
	/// per-call telemetry to the audit ledger.
	/// </summary>
	/// <param name="model">Embedding model identifier.</param>
	/// <param name="inputs">Texts to embed.</param>
	/// <param name="correlationId">Correlation token for the calling flow.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A list of vectors aligned with <paramref name="inputs"/>.</returns>
	Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
		string model,
		IReadOnlyList<string> inputs,
		Guid correlationId,
		CancellationToken cancellationToken = default);
}
