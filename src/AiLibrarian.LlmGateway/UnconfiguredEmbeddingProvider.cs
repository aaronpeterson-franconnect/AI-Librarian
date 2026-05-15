using AiLibrarian.LlmGateway.Abstractions;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// Placeholder used when the configured default embedding provider is not
/// wired or Azure OpenAI is disabled / incomplete.
/// </summary>
public sealed class UnconfiguredEmbeddingProvider : IEmbeddingProvider
{
	private readonly string _configuredDefaultProviderId;

	/// <summary>Creates an <see cref="UnconfiguredEmbeddingProvider"/>.</summary>
	public UnconfiguredEmbeddingProvider(string configuredDefaultProviderId)
	{
		_configuredDefaultProviderId = configuredDefaultProviderId;
	}

	/// <inheritdoc />
	public string ProviderId => _configuredDefaultProviderId;

	/// <inheritdoc />
	public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
		string model,
		IReadOnlyList<string> inputs,
		Guid correlationId,
		CancellationToken cancellationToken = default)
	{
		_ = model;
		_ = inputs;
		_ = correlationId;
		_ = cancellationToken;
		throw new InvalidOperationException(
			$"LLM embeddings are not available for default provider '{_configuredDefaultProviderId}'. " +
			"For Azure OpenAI, set LlmGateway:Providers:azure-openai:Enabled=true with Endpoint, " +
			"ChatDeployment, and EmbeddingDeployment (see docs/llm-providers.md).");
	}
}
