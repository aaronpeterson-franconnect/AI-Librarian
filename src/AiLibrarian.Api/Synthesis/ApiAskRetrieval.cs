using AiLibrarian.Infrastructure.Retrieval;
using AiLibrarian.LlmGateway;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.Security;

using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.Synthesis;

/// <summary>
/// API-side <see cref="IAskRetrieval"/> implementation. The actual
/// embedding + RLS-filtered chunk lookup happens in the
/// <c>/api/ask</c> route handler (it owns the embedding and RLS
/// session-variable plumbing); this class is a thin adapter that
/// just hands the already-retrieved chunks to AskGuard, so the
/// guard's pipeline shape stays uniform between API and tests.
/// </summary>
internal sealed class ApiAskRetrieval : IAskRetrieval
{
	private readonly IReadOnlyList<RetrievedChunk> _chunks;

	public ApiAskRetrieval(IReadOnlyList<RetrievedChunk> chunks)
	{
		_chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
	}

	public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(AskGuardRequest request, CancellationToken cancellationToken)
		=> Task.FromResult(_chunks);

	/// <summary>Adapt a list of <see cref="HybridChunkHit"/>s into the AskGuard contract.</summary>
	public static IReadOnlyList<RetrievedChunk> AdaptHits(IReadOnlyList<HybridChunkHit> hits)
	{
		ArgumentNullException.ThrowIfNull(hits);

		var result = new RetrievedChunk[hits.Count];
		for (var i = 0; i < hits.Count; i++)
		{
			var h = hits[i];
			result[i] = new RetrievedChunk(
				ChunkId: h.ChunkId,
				SourceId: h.SourceId,
				Classification: h.SourceClassification.ToString(),
				Department: h.SourceDepartmentId == Guid.Empty ? "unknown" : h.SourceDepartmentId.ToString("D"),
				Text: h.Excerpt);
		}

		return result;
	}
}

/// <summary>
/// API-side <see cref="IAskSynthesizer"/> implementation. Calls
/// <see cref="IChatProvider"/> with the system prompt + enveloped
/// sources + user query already prepared by AskGuard.
/// </summary>
internal sealed class ApiAskSynthesizer : IAskSynthesizer
{
	private readonly IChatProvider _chat;
	private readonly LlmGatewayOptions _llmOptions;
	private readonly AskSynthesisOptions _options;

	public ApiAskSynthesizer(
		IChatProvider chat,
		IOptions<LlmGatewayOptions> llmOptions,
		IOptions<AskSynthesisOptions>? options = null)
	{
		_chat = chat ?? throw new ArgumentNullException(nameof(chat));
		_llmOptions = llmOptions?.Value ?? throw new ArgumentNullException(nameof(llmOptions));
		_options = options?.Value ?? new AskSynthesisOptions();
	}

	public async Task<string> SynthesizeAsync(AskSynthesisRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var model = ResolveModel();

		// Assemble the user-side message: enveloped sources + the user query.
		// System prompt goes on its own message slot per OpenAI conventions
		// so the model treats it as out-of-band guidance.
		var userMessage =
			"SOURCES:\n"
			+ request.EnvelopedSources
			+ "\n\nQUESTION:\n"
			+ request.Query;

		var chatRequest = new ChatCompletionRequest(
			Model: model,
			Messages:
			[
				new ChatMessage("system", request.SystemPrompt),
				new ChatMessage("user", userMessage),
			],
			MaxTokens: _options.MaxTokens,
			Temperature: _options.Temperature,
			PersonaId: request.PersonaId,
			CorrelationId: Guid.NewGuid());

		var buffer = new System.Text.StringBuilder();
		await foreach (var chunk in _chat.StreamCompletionAsync(chatRequest, cancellationToken).ConfigureAwait(false))
		{
			if (!string.IsNullOrEmpty(chunk.DeltaContent))
			{
				buffer.Append(chunk.DeltaContent);
			}
		}

		return buffer.ToString().Trim();
	}

	private string ResolveModel()
	{
		if (!string.IsNullOrWhiteSpace(_options.Model))
		{
			return _options.Model!;
		}

		if (_llmOptions.Providers.TryGetValue("azure-openai", out var az)
			&& !string.IsNullOrWhiteSpace(az.ChatDeployment))
		{
			return az.ChatDeployment!;
		}

		return _chat.ProviderId;
	}
}

/// <summary>Knobs for synthesis-side defaults bound by <c>Ask:Synthesis</c>.</summary>
public sealed class AskSynthesisOptions
{
	/// <summary>Configuration section name.</summary>
	public const string SectionName = "Ask:Synthesis";

	/// <summary>Override the chat deployment / model used for synthesis. Falls back to LlmGateway azure-openai deployment.</summary>
	public string? Model { get; set; }

	/// <summary>Max completion tokens. Default 512.</summary>
	public int? MaxTokens { get; set; } = 512;

	/// <summary>Sampling temperature. Default 0.0 for stable answers.</summary>
	public double? Temperature { get; set; } = 0.0;

	/// <summary>How many top-k retrieved chunks to feed into the LLM. Default 8.</summary>
	public int MaxChunksForSynthesis { get; set; } = 8;
}
