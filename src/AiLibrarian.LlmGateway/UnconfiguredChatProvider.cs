using AiLibrarian.LlmGateway.Abstractions;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// Placeholder used when the configured default chat provider is not wired
/// yet (e.g. Anthropic enabled in config but not implemented) or Azure OpenAI
/// is disabled / incomplete.
/// </summary>
public sealed class UnconfiguredChatProvider : IChatProvider
{
	private readonly string _configuredDefaultProviderId;

	/// <summary>Creates an <see cref="UnconfiguredChatProvider"/>.</summary>
	public UnconfiguredChatProvider(string configuredDefaultProviderId)
	{
		_configuredDefaultProviderId = configuredDefaultProviderId;
	}

	/// <inheritdoc />
	public string ProviderId => _configuredDefaultProviderId;

	/// <inheritdoc />
	public IAsyncEnumerable<ChatCompletionChunk> StreamCompletionAsync(
		ChatCompletionRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		throw new InvalidOperationException(
			$"LLM chat is not available for default provider '{_configuredDefaultProviderId}'. " +
			"For Azure OpenAI, set LlmGateway:Providers:azure-openai:Enabled=true with Endpoint, " +
			"ChatDeployment, and EmbeddingDeployment (see docs/llm-providers.md).");
	}
}
