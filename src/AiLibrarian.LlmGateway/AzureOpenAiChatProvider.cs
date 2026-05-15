using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

using AiLibrarian.Auditing;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.LlmGateway.Internal;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// Azure OpenAI chat completion via Semantic Kernel, with per-call audit
/// metadata (ADR 0010) and no prompt/completion body capture.
/// </summary>
public sealed class AzureOpenAiChatProvider : IChatProvider
{
	private readonly Kernel _kernel;
	private readonly IAuditWriter _auditWriter;
	private readonly ILogger<AzureOpenAiChatProvider> _logger;

	/// <summary>Creates an <see cref="AzureOpenAiChatProvider"/>.</summary>
	public AzureOpenAiChatProvider(
		Kernel kernel,
		IAuditWriter auditWriter,
		ILogger<AzureOpenAiChatProvider> logger)
	{
		_kernel = kernel;
		_auditWriter = auditWriter;
		_logger = logger;
	}

	/// <inheritdoc />
	public string ProviderId => "azure-openai";

	/// <inheritdoc />
	public async IAsyncEnumerable<ChatCompletionChunk> StreamCompletionAsync(
		ChatCompletionRequest request,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		var chat = _kernel.GetRequiredService<IChatCompletionService>();
		var history = ToChatHistory(request.Messages);

		var executionSettings = new OpenAIPromptExecutionSettings
		{
			MaxTokens = request.MaxTokens,
			Temperature = request.Temperature.HasValue ? (float?)request.Temperature.Value : null,
		};

		using var activity = AiLibActivitySource.Llm.StartActivity("ailib.llm.azure-openai.chat", ActivityKind.Client);
		activity?.SetTag(AiLibActivitySource.Attributes.LlmProvider, ProviderId);
		activity?.SetTag(AiLibActivitySource.Attributes.LlmModel, request.Model);
		activity?.SetTag(AiLibActivitySource.Attributes.CorrelationId, request.CorrelationId);
		if (request.PersonaId is { } pid)
		{
			activity?.SetTag(AiLibActivitySource.Attributes.PersonaId, pid);
		}

		var sw = Stopwatch.StartNew();
		var sb = new StringBuilder();
		string? finishReason = null;
		int promptTokens = 0;
		int completionTokens = 0;

		await foreach (var update in chat.GetStreamingChatMessageContentsAsync(
				history,
				executionSettings,
				kernel: null,
				cancellationToken).ConfigureAwait(false))
		{
			if (!string.IsNullOrEmpty(update.Content))
			{
				sb.Append(update.Content);
				yield return new ChatCompletionChunk(update.Content, null);
			}

			if (update.Metadata is not null
				&& update.Metadata.TryGetValue("FinishReason", out var fr)
				&& fr is string frs)
			{
				finishReason = frs;
			}

			TryReadUsage(update.Metadata, ref promptTokens, ref completionTokens);
		}

		sw.Stop();

		activity?.SetTag(AiLibActivitySource.Attributes.LlmTokensIn, promptTokens);
		activity?.SetTag(AiLibActivitySource.Attributes.LlmTokensOut, completionTokens);
		activity?.SetTag(AiLibActivitySource.Attributes.LlmLatencyMs, sw.ElapsedMilliseconds);

		var telemetry = new LlmTelemetry(
			Provider: ProviderId,
			Model: request.Model,
			PromptTokens: promptTokens,
			CompletionTokens: completionTokens,
			CostEstimateUsd: null,
			LatencyMs: (int)sw.ElapsedMilliseconds,
			PersonaId: request.PersonaId);

		var auditEvent = new AuditEvent(
			Id: Guid.NewGuid(),
			OccurredAt: DateTimeOffset.UtcNow,
			ActorUserId: AuditConstants.SystemUserId,
			ActorRole: null,
			OriginatedBy: null,
			DepartmentId: null,
			EventType: "query",
			EventSubtype: "llm.chat.stream",
			TargetKind: "llm_call",
			TargetId: null,
			CorrelationId: request.CorrelationId,
			Outcome: EventOutcome.Success,
			ErrorClass: null,
			Llm: telemetry,
			Details: new Dictionary<string, object?>
			{
				["provider"] = ProviderId,
				["model"] = request.Model,
				["finish_reason"] = finishReason,
				["generated_chars"] = sb.Length,
			});

		// Post-stream telemetry — the user has already received the response,
		// so audit failures must not throw an unhandled exception out of the
		// IAsyncEnumerable. Best-effort per ADR 0010.
		await _auditWriter.WriteAsync(auditEvent, AuditCriticality.BestEffort, cancellationToken).ConfigureAwait(false);

		_logger.LogDebug(
			"LLM chat stream complete provider={Provider} model={Model} ms={Ms} promptTokens={Prompt} completionTokens={Completion}",
			ProviderId,
			request.Model,
			sw.ElapsedMilliseconds,
			promptTokens,
			completionTokens);

		yield return new ChatCompletionChunk(string.Empty, finishReason);
	}

	private static ChatHistory ToChatHistory(IReadOnlyList<ChatMessage> messages)
	{
		var history = new ChatHistory();
		foreach (var m in messages)
		{
			switch (m.Role.Trim().ToLowerInvariant())
			{
				case "system":
					history.AddSystemMessage(m.Content);
					break;
				case "assistant":
					history.AddAssistantMessage(m.Content);
					break;
				case "user":
				default:
					history.AddUserMessage(m.Content);
					break;
			}
		}

		return history;
	}

	private static void TryReadUsage(IReadOnlyDictionary<string, object?>? metadata, ref int promptTokens, ref int completionTokens)
	{
		if (metadata is null)
		{
			return;
		}

		// SK / OpenAI connectors surface usage in metadata keys that vary by version;
		// best-effort extraction keeps the audit ledger useful without coupling to internals.
		foreach (var (key, value) in metadata)
		{
			if (value is null)
			{
				continue;
			}

			var lowered = key.ToLowerInvariant();
			if (lowered.Contains("prompt", StringComparison.Ordinal) && lowered.Contains("token", StringComparison.Ordinal))
			{
				TryAssignInt(value, ref promptTokens);
			}
			else if (lowered.Contains("completion", StringComparison.Ordinal) && lowered.Contains("token", StringComparison.Ordinal))
			{
				TryAssignInt(value, ref completionTokens);
			}
			else if (lowered is "usage" or "openai.usage")
			{
				// Some versions embed a usage object as string or anonymous type — skip deep reflection in v1.
			}
		}
	}

	private static void TryAssignInt(object value, ref int target)
	{
		switch (value)
		{
			case int i:
				target = i;
				break;
			case long l:
				target = (int)l;
				break;
			case string s when int.TryParse(s, out var parsed):
				target = parsed;
				break;
		}
	}
}
