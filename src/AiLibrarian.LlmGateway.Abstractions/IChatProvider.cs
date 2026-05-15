namespace AiLibrarian.LlmGateway.Abstractions;

/// <summary>
/// Provider-agnostic chat-completion contract — the gateway's primary
/// surface per ADR 0003. Every implementation routes through Microsoft
/// Semantic Kernel; this interface keeps callers off the SK API
/// surface so we can hot-swap connectors without churning callers.
/// </summary>
public interface IChatProvider
{
	/// <summary>The provider this implementation represents.</summary>
	string ProviderId { get; }

	/// <summary>
	/// Stream a chat completion. Implementations must emit per-call
	/// telemetry (provider, model, token counts, latency, persona)
	/// to the audit ledger via <c>IAuditWriter</c>; metadata only
	/// per ADR 0010.
	/// </summary>
	/// <param name="request">Request envelope.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>An async stream of partial response fragments.</returns>
	IAsyncEnumerable<ChatCompletionChunk> StreamCompletionAsync(
		ChatCompletionRequest request,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// A request to the chat provider. Carries the persona context per
/// ADR 0015; the provider implementation applies any persona-specific
/// synthesis-style adjustments.
/// </summary>
/// <param name="Model">Target model identifier.</param>
/// <param name="Messages">Conversation messages in order.</param>
/// <param name="MaxTokens">Optional token cap for the completion.</param>
/// <param name="Temperature">Optional sampling temperature.</param>
/// <param name="PersonaId">Optional persona context for per-call telemetry.</param>
/// <param name="CorrelationId">Correlation token threading audit, retrieval,
/// and synthesis events for the same flow.</param>
public sealed record ChatCompletionRequest(
	string Model,
	IReadOnlyList<ChatMessage> Messages,
	int? MaxTokens,
	double? Temperature,
	Guid? PersonaId,
	Guid CorrelationId);

/// <summary>A single message in a chat-completion request.</summary>
/// <param name="Role">Speaker role (e.g., <c>system</c>, <c>user</c>, <c>assistant</c>).</param>
/// <param name="Content">Plain-text content.</param>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// One chunk of a streamed completion. The provider may emit many
/// per request; assemble them in order to reconstruct the full text.
/// </summary>
/// <param name="DeltaContent">Newly-generated text since the prior chunk.</param>
/// <param name="FinishReason">Final reason on the last chunk; null otherwise.</param>
public sealed record ChatCompletionChunk(string DeltaContent, string? FinishReason);
