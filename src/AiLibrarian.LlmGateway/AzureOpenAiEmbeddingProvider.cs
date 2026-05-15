using System.Diagnostics;
using System.Linq;

using AiLibrarian.Auditing;
using AiLibrarian.LlmGateway.Abstractions;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// Azure OpenAI text embeddings via Semantic Kernel, with per-call audit metadata.
/// </summary>
public sealed class AzureOpenAiEmbeddingProvider : IEmbeddingProvider
{
	private readonly Kernel _kernel;
	private readonly IAuditWriter _auditWriter;
	private readonly ILogger<AzureOpenAiEmbeddingProvider> _logger;

	/// <summary>Creates an <see cref="AzureOpenAiEmbeddingProvider"/>.</summary>
	public AzureOpenAiEmbeddingProvider(
		Kernel kernel,
		IAuditWriter auditWriter,
		ILogger<AzureOpenAiEmbeddingProvider> logger)
	{
		_kernel = kernel;
		_auditWriter = auditWriter;
		_logger = logger;
	}

	/// <inheritdoc />
	public string ProviderId => "azure-openai";

	/// <inheritdoc />
	public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
		string model,
		IReadOnlyList<string> inputs,
		Guid correlationId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(model);
		ArgumentNullException.ThrowIfNull(inputs);
		if (inputs.Count == 0)
		{
			return Array.Empty<ReadOnlyMemory<float>>();
		}

		var embedder = _kernel.GetRequiredService<ITextEmbeddingGenerationService>();

		using var activity = AiLibActivitySource.Llm.StartActivity("ailib.llm.azure-openai.embed", ActivityKind.Client);
		activity?.SetTag(AiLibActivitySource.Attributes.LlmProvider, ProviderId);
		activity?.SetTag(AiLibActivitySource.Attributes.LlmModel, model);
		activity?.SetTag(AiLibActivitySource.Attributes.CorrelationId, correlationId);
		activity?.SetTag("ailib.llm.embed.input_count", inputs.Count);

		var sw = Stopwatch.StartNew();
		var vectorList = await embedder.GenerateEmbeddingsAsync(
				inputs.ToList(),
				cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		sw.Stop();
		activity?.SetTag(AiLibActivitySource.Attributes.LlmLatencyMs, sw.ElapsedMilliseconds);

		var promptTokens = EstimateTokenCount(inputs);

		var telemetry = new LlmTelemetry(
			Provider: ProviderId,
			Model: model,
			PromptTokens: promptTokens,
			CompletionTokens: 0,
			CostEstimateUsd: null,
			LatencyMs: (int)sw.ElapsedMilliseconds,
			PersonaId: null);

		var auditEvent = new AuditEvent(
			Id: Guid.NewGuid(),
			OccurredAt: DateTimeOffset.UtcNow,
			ActorUserId: AuditConstants.SystemUserId,
			ActorRole: null,
			OriginatedBy: null,
			DepartmentId: null,
			EventType: "query",
			EventSubtype: "llm.embeddings",
			TargetKind: "llm_call",
			TargetId: null,
			CorrelationId: correlationId,
			Outcome: EventOutcome.Success,
			ErrorClass: null,
			Llm: telemetry,
			Details: new Dictionary<string, object?>
			{
				["provider"] = ProviderId,
				["model"] = model,
				["input_count"] = inputs.Count,
			});

		// Embedding telemetry — caller has already received vectors; audit
		// is best-effort per ADR 0010's read-side classification.
		await _auditWriter.WriteAsync(auditEvent, AuditCriticality.BestEffort, cancellationToken).ConfigureAwait(false);

		_logger.LogDebug(
			"LLM embeddings complete provider={Provider} model={Model} ms={Ms} inputs={Inputs}",
			ProviderId,
			model,
			sw.ElapsedMilliseconds,
			inputs.Count);

		return vectorList.ToArray();
	}

	private static int EstimateTokenCount(IReadOnlyList<string> inputs)
	{
		var chars = 0;
		foreach (var s in inputs)
		{
			chars += s.Length;
		}

		return Math.Max(1, chars / 4);
	}
}
