namespace AiLibrarian.LlmGateway.Abstractions;

/// <summary>
/// Provider-agnostic reranker contract per ADR 0003. Used by retrieval
/// after the hybrid (vector + lexical) candidate set is gathered. The
/// persona-aware reranking step described in ADR 0015 is a layer on
/// top of this contract — the reranker scores candidates against the
/// query; the persona retrieval profile multiplies those scores.
/// </summary>
public interface IRerankProvider
{
	/// <summary>The provider this implementation represents.</summary>
	string ProviderId { get; }

	/// <summary>
	/// Rerank candidates against a query.
	/// </summary>
	/// <param name="model">Reranker model identifier.</param>
	/// <param name="query">Query text.</param>
	/// <param name="candidates">Candidate documents with provider-specific keys.</param>
	/// <param name="correlationId">Correlation token for the calling flow.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Candidate keys with their reranker scores, in score order (highest first).</returns>
	Task<IReadOnlyList<RerankResult>> RerankAsync(
		string model,
		string query,
		IReadOnlyList<RerankCandidate> candidates,
		Guid correlationId,
		CancellationToken cancellationToken = default);
}

/// <summary>One candidate fed to the reranker.</summary>
/// <param name="Key">Caller-side identifier; the reranker echoes this.</param>
/// <param name="Text">Text to score against the query.</param>
public sealed record RerankCandidate(string Key, string Text);

/// <summary>The reranker's score for one candidate.</summary>
/// <param name="Key">Echoed key from the input.</param>
/// <param name="Score">Score in [0, 1]; higher is more relevant.</param>
public sealed record RerankResult(string Key, double Score);
