using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using AiLibrarian.Mcp.Auth;
using AiLibrarian.Mcp.Internal;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Mcp;

/// <summary>
/// Calls the Librarian API. Each request fetches a fresh bearer token
/// from <see cref="IBearerTokenProvider"/> so long-lived stdio MCP
/// sessions stay authenticated as MSAL refreshes the access token in
/// the background. The previous "read AILIB_ACCESS_TOKEN once" path
/// is preserved as the env-provider fallback.
/// </summary>
public sealed class AiLibrarianApiClient
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private readonly HttpClient _http;
	private readonly IOptions<McpApiOptions> _options;
	private readonly IBearerTokenProvider _tokenProvider;
	private readonly ILogger<AiLibrarianApiClient> _logger;

	public AiLibrarianApiClient(
		HttpClient http,
		IOptions<McpApiOptions> options,
		IBearerTokenProvider tokenProvider,
		ILogger<AiLibrarianApiClient> logger)
	{
		_http = http;
		_options = options;
		_tokenProvider = tokenProvider;
		_logger = logger;
	}

	public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Value.BaseUrl);

	public async Task<string> SearchHybridAsync(string query, CancellationToken cancellationToken)
	{
		if (!IsConfigured)
		{
			return JsonSerializer.Serialize(
				new
				{
					error = "Api:BaseUrl is not configured.",
					hint = "Set Api:BaseUrl in appsettings.json next to AiLibrarian.Mcp.dll or configure deployment.",
				},
				JsonOptions);
		}

		using var req = new HttpRequestMessage(HttpMethod.Post, "api/search/hybrid")
		{
			Content = JsonContent.Create(new { query }, options: JsonOptions),
		};
		await AddBearerIfPresent(req, cancellationToken).ConfigureAwait(false);

		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Hybrid search HTTP request failed");
			return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
		}

		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			_logger.LogWarning(
				"Hybrid search API status={Status} body={Body}",
				(int)resp.StatusCode,
				body.Length > 500 ? body[..500] : body);
		}

		return body;
	}

	public async Task<string> GetSourceAsync(Guid sourceId, CancellationToken cancellationToken)
	{
		if (!IsConfigured)
		{
			return JsonSerializer.Serialize(
				new { error = "Api:BaseUrl is not configured." },
				JsonOptions);
		}

		using var req = new HttpRequestMessage(
			HttpMethod.Get,
			new Uri($"api/sources/{sourceId:D}", UriKind.Relative));
		await AddBearerIfPresent(req, cancellationToken).ConfigureAwait(false);

		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Get source HTTP request failed");
			return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
		}

		if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			return JsonSerializer.Serialize(
				new { sourceId = sourceId.ToString("D"), found = false },
				JsonOptions);
		}

		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			_logger.LogWarning("Get source API status={Status}", (int)resp.StatusCode);
		}

		return body;
	}

	public async Task<string> ListDepartmentsAsync(CancellationToken cancellationToken)
	{
		if (!IsConfigured)
		{
			return JsonSerializer.Serialize(
				new { error = "Api:BaseUrl is not configured." },
				JsonOptions);
		}

		using var req = new HttpRequestMessage(
			HttpMethod.Get,
			new Uri("api/departments", UriKind.Relative));
		await AddBearerIfPresent(req, cancellationToken).ConfigureAwait(false);

		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "List departments HTTP request failed");
			return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
		}

		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			_logger.LogWarning("List departments API status={Status}", (int)resp.StatusCode);
		}

		return body;
	}

	public async Task<string> UploadFileAsync(
		string filePath,
		Guid departmentId,
		string classification,
		string? title,
		string? contentType,
		Guid? contributorId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(classification);

		if (!IsConfigured)
		{
			return JsonSerializer.Serialize(
				new { error = "Api:BaseUrl is not configured." },
				JsonOptions);
		}

		if (!File.Exists(filePath))
		{
			return JsonSerializer.Serialize(
				new { error = $"File not found: {filePath}" },
				JsonOptions);
		}

		var resolvedContentType = string.IsNullOrWhiteSpace(contentType)
			? MimeTypeMap.ResolveByPath(filePath)
			: contentType.Trim();

		await using var fileStream = File.OpenRead(filePath);

		using var form = new MultipartFormDataContent();
		var streamContent = new StreamContent(fileStream);
		streamContent.Headers.ContentType = MediaTypeHeaderValue.TryParse(resolvedContentType, out var parsed)
			? parsed
			: new MediaTypeHeaderValue("application/octet-stream");
		form.Add(streamContent, "file", Path.GetFileName(filePath));
		form.Add(new StringContent(departmentId.ToString("D")), "departmentId");
		form.Add(new StringContent(classification), "classification");
		if (!string.IsNullOrWhiteSpace(title))
		{
			form.Add(new StringContent(title), "title");
		}

		if (contributorId is { } cid && cid != Guid.Empty)
		{
			form.Add(new StringContent(cid.ToString("D")), "contributorId");
		}

		using var req = new HttpRequestMessage(HttpMethod.Post, "api/portal/sources/upload")
		{
			Content = form,
		};
		await AddBearerIfPresent(req, cancellationToken).ConfigureAwait(false);

		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Upload HTTP request failed for {Path}", filePath);
			return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
		}

		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			_logger.LogWarning("Upload API status={Status}", (int)resp.StatusCode);
		}

		return body;
	}

	public async Task<string> ListRecentChangesAsync(int limit, CancellationToken cancellationToken)
	{
		if (!IsConfigured)
		{
			return JsonSerializer.Serialize(
				new { error = "Api:BaseUrl is not configured." },
				JsonOptions);
		}

		var clamped = Math.Clamp(limit <= 0 ? 25 : limit, 1, 100);
		using var req = new HttpRequestMessage(
			HttpMethod.Get,
			new Uri($"api/audit/recent?limit={clamped}", UriKind.Relative));
		await AddBearerIfPresent(req, cancellationToken).ConfigureAwait(false);

		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "List recent changes HTTP request failed");
			return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
		}

		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			_logger.LogWarning("List recent changes API status={Status}", (int)resp.StatusCode);
		}

		return body;
	}

	public async Task<string> EnqueueIngestAsync(
		string blobUri,
		string? contentType,
		string? originalFileName,
		string? correlationId,
		Guid? sourceId,
		CancellationToken cancellationToken)
	{
		if (!IsConfigured)
		{
			return JsonSerializer.Serialize(
				new { error = "Api:BaseUrl is not configured." },
				JsonOptions);
		}

		using var req = new HttpRequestMessage(HttpMethod.Post, "api/ingest/enqueue")
		{
			Content = JsonContent.Create(
				new
				{
					blobUri,
					contentType,
					originalFileName,
					correlationId,
					sourceId,
				},
				options: JsonOptions),
		};
		await AddBearerIfPresent(req, cancellationToken).ConfigureAwait(false);

		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Enqueue ingest HTTP request failed");
			return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
		}

		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
		{
			_logger.LogWarning("Enqueue ingest API status={Status}", (int)resp.StatusCode);
		}

		return body;
	}

	/// <summary>POST <c>/api/ask</c>. The API runs the AskGuard pipeline; this client just relays.</summary>
	public async Task<string> AskAsync(
		string query,
		int? maxChunks,
		string? personaId,
		CancellationToken cancellationToken)
	{
		if (!IsConfigured)
		{
			return JsonSerializer.Serialize(
				new { error = "Api:BaseUrl is not configured." },
				JsonOptions);
		}

		using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("api/ask", UriKind.Relative))
		{
			Content = JsonContent.Create(new { query, maxChunks, personaId }, options: JsonOptions),
		};
		await AddBearerIfPresent(req, cancellationToken).ConfigureAwait(false);

		HttpResponseMessage resp;
		try
		{
			resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Ask HTTP request failed");
			return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
		}

		var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
		{
			_logger.LogWarning("Ask API status={Status} body={Body}",
				(int)resp.StatusCode, body.Length > 500 ? body[..500] : body);
		}

		return body;
	}

	private async Task AddBearerIfPresent(HttpRequestMessage req, CancellationToken cancellationToken)
	{
		var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
		if (!string.IsNullOrWhiteSpace(token))
		{
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}
	}
}
