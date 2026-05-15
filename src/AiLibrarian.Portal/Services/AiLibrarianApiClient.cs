using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiLibrarian.Portal.Services;

/// <summary>
/// HTTP-backed <see cref="IAiLibrarianApiClient"/> that wraps the
/// named <c>"Api"</c> <see cref="HttpClient"/> registered in
/// <c>Program.cs</c>. Phase 1 doesn't yet thread Entra access tokens
/// through — Phase 2 hardening adds <c>ITokenAcquisition</c> + an
/// <c>AuthenticationDelegatingHandler</c> on the HttpClientFactory.
/// </summary>
public sealed class AiLibrarianApiClient : IAiLibrarianApiClient
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly IHttpClientFactory _httpClientFactory;

	/// <summary>Creates the client.</summary>
	public AiLibrarianApiClient(IHttpClientFactory httpClientFactory)
	{
		_httpClientFactory = httpClientFactory;
	}

	/// <inheritdoc />
	public async Task<UploadOutcome> UploadAsync(
		Stream content,
		string fileName,
		string? contentType,
		Guid departmentId,
		string classification,
		string? title,
		Guid? contributorId,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(content);

		using var form = new MultipartFormDataContent();
		var streamContent = new StreamContent(content);
		streamContent.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var parsed)
			? parsed
			: new MediaTypeHeaderValue("application/octet-stream");
		form.Add(streamContent, "file", string.IsNullOrWhiteSpace(fileName) ? "upload.bin" : fileName);
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

		var http = _httpClientFactory.CreateClient("Api");
		using var response = await http.PostAsync(
				new Uri("api/portal/sources/upload", UriKind.Relative),
				form,
				cancellationToken)
			.ConfigureAwait(false);

		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			return new UploadOutcome(false, (int)response.StatusCode, body, null, null, null);
		}

		UploadResponseDto? payload;
		try
		{
			payload = JsonSerializer.Deserialize<UploadResponseDto>(body, JsonOptions);
		}
		catch (JsonException ex)
		{
			return new UploadOutcome(false, (int)response.StatusCode, $"Malformed response: {ex.Message}", null, null, null);
		}

		if (payload is null)
		{
			return new UploadOutcome(false, (int)response.StatusCode, "Empty response body.", null, null, null);
		}

		// Chain the ingest enqueue. The architecture keeps these endpoints
		// separate so non-Portal callers (bulk import, MCP) can submit
		// without auto-processing, but the Portal's UX expectation is that
		// "upload" means "upload AND process." Partial failure here is
		// surfaced via EnqueueSucceeded so the UI can warn the operator
		// without claiming the overall flow failed -- the source row
		// exists; only the processing handoff didn't fire.
		//
		// Skip the enqueue when the API reports a duplicate (server-side
		// checksum match returned the existing source row). The original
		// source already went through the pipeline; re-running it produces
		// no new chunks and only burns a Service Bus message. The UI still
		// shows a success banner, but EnqueueAttempted=false makes clear
		// nothing was queued.
		if (payload.DuplicateOfExisting)
		{
			return new UploadOutcome(
				Success: true,
				StatusCode: (int)response.StatusCode,
				Message: string.Empty,
				SourceId: payload.SourceId,
				BlobUri: payload.BlobUri,
				Title: payload.Title,
				EnqueueAttempted: false,
				EnqueueSucceeded: false,
				EnqueueMessage: "duplicate of existing source; not re-queued");
		}

		var (enqueueSucceeded, enqueueMessage) = await TryEnqueueIngestAsync(
			payload.BlobUri,
			payload.SourceId,
			payload.ContentType,
			payload.OriginalFileName,
			cancellationToken).ConfigureAwait(false);

		return new UploadOutcome(
			Success: true,
			StatusCode: (int)response.StatusCode,
			Message: string.Empty,
			SourceId: payload.SourceId,
			BlobUri: payload.BlobUri,
			Title: payload.Title,
			EnqueueAttempted: true,
			EnqueueSucceeded: enqueueSucceeded,
			EnqueueMessage: enqueueMessage);
	}

	private async Task<(bool Succeeded, string Message)> TryEnqueueIngestAsync(
		string blobUri,
		Guid sourceId,
		string? contentType,
		string originalFileName,
		CancellationToken cancellationToken)
	{
		var http = _httpClientFactory.CreateClient("Api");
		using var content = JsonContent.Create(
			new
			{
				blobUri,
				sourceId,
				contentType,
				originalFileName,
			},
			options: JsonOptions);

		try
		{
			using var response = await http.PostAsync(
					new Uri("api/ingest/enqueue", UriKind.Relative),
					content,
					cancellationToken)
				.ConfigureAwait(false);

			if (response.IsSuccessStatusCode)
			{
				return (true, "queued for ingest");
			}

			var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var trimmed = detail.Length > 240 ? detail[..240] + "..." : detail;
			return (false, $"enqueue returned {(int)response.StatusCode}: {trimmed}");
		}
		catch (HttpRequestException ex)
		{
			return (false, $"enqueue network error: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<SourcesPage> ListSourcesAsync(
		Guid? departmentId,
		int limit,
		int offset,
		CancellationToken cancellationToken = default)
	{
		var http = _httpClientFactory.CreateClient("Api");
		var path = $"api/sources?limit={limit}&offset={offset}";
		if (departmentId.HasValue)
		{
			path += $"&departmentId={departmentId.Value:D}";
		}

		var page = await http
			.GetFromJsonAsync<SourcesPage>(new Uri(path, UriKind.Relative), JsonOptions, cancellationToken)
			.ConfigureAwait(false);

		return page ?? new SourcesPage(Array.Empty<SourceListItem>());
	}

	/// <inheritdoc />
	public async Task<SourceListItem?> GetSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
	{
		var http = _httpClientFactory.CreateClient("Api");
		using var response = await http
			.GetAsync(new Uri($"api/sources/{sourceId:D}", UriKind.Relative), cancellationToken)
			.ConfigureAwait(false);

		if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			return null;
		}

		response.EnsureSuccessStatusCode();
		return await response.Content
			.ReadFromJsonAsync<SourceListItem>(JsonOptions, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<DepartmentListItem>> ListDepartmentsAsync(CancellationToken cancellationToken = default)
	{
		var http = _httpClientFactory.CreateClient("Api");
		var page = await http
			.GetFromJsonAsync<DepartmentsPageDto>(new Uri("api/departments", UriKind.Relative), JsonOptions, cancellationToken)
			.ConfigureAwait(false);

		return page?.Items ?? Array.Empty<DepartmentListItem>();
	}

	private sealed record UploadResponseDto(
		string BlobUri,
		string OriginalFileName,
		string? ContentType,
		Guid SourceId,
		Guid DepartmentId,
		string Classification,
		string Title,
		bool DuplicateOfExisting = false,
		string? Sha256 = null);

	private sealed record DepartmentsPageDto(IReadOnlyList<DepartmentListItem> Items);
}
