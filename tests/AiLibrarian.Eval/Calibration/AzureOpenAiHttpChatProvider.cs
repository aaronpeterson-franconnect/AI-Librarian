using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiLibrarian.LlmGateway.Abstractions;

namespace AiLibrarian.Eval.Calibration;

/// <summary>
/// Minimal HTTP-only <see cref="IChatProvider"/> for the live
/// calibration test. Hits Azure OpenAI's
/// <c>/openai/deployments/{deployment}/chat/completions</c> endpoint
/// directly so the Eval project doesn't need to drag the whole
/// <c>AiLibrarian.LlmGateway</c> (Semantic Kernel + Polly + audit
/// telemetry) into a test-time dependency graph.
///
/// <para><b>Why not just reference the gateway?</b> The gateway is
/// process-internal infrastructure with its own DI / startup probe /
/// audit wiring; pulling it into the test project would couple eval
/// to operational concerns (audit ledger configuration, gateway
/// startup diagnostics) that have nothing to do with measuring
/// inter-rater agreement. This shim does one thing: send the
/// system + user messages and stream back the completion text.</para>
///
/// <para>Authentication: API key only in this shim. CI passes
/// <c>AZURE_OPENAI_API_KEY</c> via a secret; local operators export
/// the same env var or run against a key-vaulted endpoint. Managed-
/// identity / DefaultAzureCredential support is the gateway's job —
/// not duplicated here.</para>
/// </summary>
public sealed class AzureOpenAiHttpChatProvider : IChatProvider
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly HttpClient _http;
	private readonly AzureOpenAiHttpChatProviderOptions _options;

	/// <inheritdoc />
	public string ProviderId => "azure-openai-eval-shim";

	/// <summary>Creates the shim. Caller owns the <see cref="HttpClient"/> lifetime.</summary>
	public AzureOpenAiHttpChatProvider(HttpClient http, AzureOpenAiHttpChatProviderOptions options)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
		_options = options ?? throw new ArgumentNullException(nameof(options));
		ArgumentException.ThrowIfNullOrWhiteSpace(_options.Endpoint);
		ArgumentException.ThrowIfNullOrWhiteSpace(_options.ApiKey);
		ArgumentException.ThrowIfNullOrWhiteSpace(_options.ChatDeployment);
	}

	/// <inheritdoc />
	public async IAsyncEnumerable<ChatCompletionChunk> StreamCompletionAsync(
		ChatCompletionRequest request,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		// Build the Azure OpenAI request envelope. The shim does not
		// stream over SSE -- it makes a single non-streaming call and
		// yields the whole content as one chunk. The grader collects
		// chunks into a buffer anyway, so the surface contract is
		// satisfied.
		var requestEnvelope = new AzureChatRequest(
			Messages: request.Messages.Select(m => new AzureMessage(m.Role, m.Content)).ToList(),
			Temperature: request.Temperature,
			MaxTokens: request.MaxTokens);

		var endpoint = BuildEndpoint(_options.Endpoint!, _options.ChatDeployment!, _options.ApiVersion);

		using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
		{
			Content = JsonContent.Create(requestEnvelope, options: JsonOptions),
		};
		req.Headers.Add("api-key", _options.ApiKey);
		// Accept JSON only; the shim does not negotiate SSE.
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

		using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			var error = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			throw new InvalidOperationException(
				$"Azure OpenAI chat completion failed ({(int)resp.StatusCode}): "
				+ (error.Length > 500 ? error[..500] : error));
		}

		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		AzureChatResponse? dto;
		try
		{
			dto = JsonSerializer.Deserialize<AzureChatResponse>(body, JsonOptions);
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException(
				$"Azure OpenAI returned a body that failed to parse: {ex.Message}",
				ex);
		}

		var firstChoice = dto?.Choices is { Count: > 0 } choices ? choices[0] : null;
		var content = firstChoice?.Message?.Content ?? string.Empty;
		var finish = firstChoice?.FinishReason;
		yield return new ChatCompletionChunk(DeltaContent: content, FinishReason: finish);
	}

	private static string BuildEndpoint(string baseEndpoint, string deployment, string apiVersion)
	{
		var trimmed = baseEndpoint.TrimEnd('/');
		var encodedDeployment = Uri.EscapeDataString(deployment);
		var encodedVersion = Uri.EscapeDataString(apiVersion);
		return string.Format(
			CultureInfo.InvariantCulture,
			"{0}/openai/deployments/{1}/chat/completions?api-version={2}",
			trimmed,
			encodedDeployment,
			encodedVersion);
	}

	// --- wire DTOs (private; the wire format is Azure OpenAI's, not ours)

	private sealed record AzureChatRequest(
		IReadOnlyList<AzureMessage> Messages,
		double? Temperature,
		int? MaxTokens);

	private sealed record AzureMessage(string Role, string Content);

	private sealed record AzureChatResponse(IReadOnlyList<AzureChoice>? Choices);

	private sealed record AzureChoice(AzureMessage? Message, string? FinishReason);
}

/// <summary>Configuration for <see cref="AzureOpenAiHttpChatProvider"/>.</summary>
public sealed class AzureOpenAiHttpChatProviderOptions
{
	/// <summary>Endpoint base URL (e.g. <c>https://my-azure-openai.openai.azure.com</c>).</summary>
	public string? Endpoint { get; set; }

	/// <summary>API key. Required by this shim; managed-identity support is the gateway's job.</summary>
	public string? ApiKey { get; set; }

	/// <summary>Azure OpenAI deployment name (NOT the model name).</summary>
	public string? ChatDeployment { get; set; }

	/// <summary>API version. Defaults to a recent stable preview.</summary>
	public string ApiVersion { get; set; } = "2024-08-01-preview";

	/// <summary>
	/// Build from environment variables. Returns null when any required
	/// variable is missing — callers use this as the "skip live test"
	/// signal.
	/// </summary>
	public static AzureOpenAiHttpChatProviderOptions? FromEnvironment()
	{
		var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
		var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
		var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT");

		if (string.IsNullOrWhiteSpace(endpoint)
			|| string.IsNullOrWhiteSpace(apiKey)
			|| string.IsNullOrWhiteSpace(deployment))
		{
			return null;
		}

		var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION");
		return new AzureOpenAiHttpChatProviderOptions
		{
			Endpoint = endpoint,
			ApiKey = apiKey,
			ChatDeployment = deployment,
			ApiVersion = string.IsNullOrWhiteSpace(apiVersion) ? "2024-08-01-preview" : apiVersion,
		};
	}
}
