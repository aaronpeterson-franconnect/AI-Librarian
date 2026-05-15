using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using AiLibrarian.Mcp.Internal;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace AiLibrarian.Mcp;

/// <summary>MCP tools call the Librarian HTTP API when <c>Api:BaseUrl</c> is set (see ADR 0004).</summary>
[McpServerToolType]
public sealed class LibrarianMcpTools
{
	private readonly McpWorkstationContext _workstation;
	private readonly AiLibrarianApiClient _api;
	private readonly ILogger<LibrarianMcpTools> _logger;

	public LibrarianMcpTools(
		McpWorkstationContext workstation,
		AiLibrarianApiClient api,
		ILogger<LibrarianMcpTools> logger)
	{
		_workstation = workstation;
		_api = api;
		_logger = logger;
	}

	[McpServerTool(Name = "search")]
	[Description("Hybrid search (vector + full-text) against the Librarian corpus via POST /api/search/hybrid. Requires Api:BaseUrl and AILIB_ACCESS_TOKEN when the API uses Entra.")]
	public async Task<string> Search(string query)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(query);
		_logger.LogDebug("search query length={Len} authenticated={Auth}", query.Length, _workstation.HasBearerToken);
		var payload = await _api.SearchHybridAsync(query.Trim(), CancellationToken.None).ConfigureAwait(false);
		return AppendWorkstationFooter(payload);
	}

	[McpServerTool(Name = "ask")]
	[Description("Ask the Librarian a natural-language question. Synthesizes an answer from retrieved chunks via POST /api/ask. The server-side AskGuard (ADR 0017) applies the six controls: query byte cap, rate limit per caller, source envelope, canonical system prompt, secret redaction (shadow mode by default), and full-call audit. Refusals are returned as structured JSON with a reason code.")]
	public async Task<string> Ask(
		string query,
		int? maxChunks = null,
		string? personaId = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(query);
		_logger.LogDebug("ask query length={Len} authenticated={Auth}", query.Length, _workstation.HasBearerToken);
		var payload = await _api.AskAsync(query.Trim(), maxChunks, personaId, CancellationToken.None).ConfigureAwait(false);
		return AppendWorkstationFooter(payload);
	}

	[McpServerTool(Name = "enqueue_source")]
	[Description("Queue an ingest job (POST /api/ingest/enqueue): HTTPS blob URI plus optional MIME, file name, correlation id, and catalog source id.")]
	public async Task<string> EnqueueSource(
		string blobUri,
		string? contentType = null,
		string? originalFileName = null,
		string? correlationId = null,
		string? sourceId = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(blobUri);
		Guid? sid = Guid.TryParse(sourceId, out var g) ? g : null;
		_logger.LogDebug("enqueue_source blobUri length={Len}", blobUri.Length);
		var payload = await _api.EnqueueIngestAsync(
				blobUri.Trim(),
				contentType,
				originalFileName,
				correlationId,
				sid,
				CancellationToken.None)
			.ConfigureAwait(false);
		return AppendWorkstationFooter(payload);
	}

	[McpServerTool(Name = "submit_source")]
	[Description("Upload a local file to the Librarian corpus AND queue it for ingest in one step. Reads the file from the workstation, POSTs to /api/portal/sources/upload to create the source row, then POSTs /api/ingest/enqueue with the new sourceId. Returns both the upload result and the queue acknowledgment.")]
	public async Task<string> SubmitSource(
		string filePath,
		string departmentId,
		string? classification = null,
		string? title = null,
		string? contentType = null,
		string? contributorId = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);

		if (!File.Exists(filePath))
		{
			return JsonSerializer.Serialize(new
			{
				error = $"File not found: {filePath}",
				workstation = WorkstationPayload(),
			});
		}

		if (!Guid.TryParse(departmentId, CultureInfo.InvariantCulture, out var deptId) || deptId == Guid.Empty)
		{
			return JsonSerializer.Serialize(new
			{
				error = "departmentId must be a non-empty GUID.",
				workstation = WorkstationPayload(),
			});
		}

		var effectiveClassification = string.IsNullOrWhiteSpace(classification)
			? "Internal"
			: classification.Trim();

		Guid? contributorOid = null;
		if (!string.IsNullOrWhiteSpace(contributorId))
		{
			if (!Guid.TryParse(contributorId, CultureInfo.InvariantCulture, out var parsed) || parsed == Guid.Empty)
			{
				return JsonSerializer.Serialize(new
				{
					error = "contributorId must be a GUID when supplied.",
					workstation = WorkstationPayload(),
				});
			}

			contributorOid = parsed;
		}

		var effectiveContentType = string.IsNullOrWhiteSpace(contentType)
			? MimeTypeMap.ResolveByPath(filePath)
			: contentType.Trim();
		var fileName = Path.GetFileName(filePath);

		_logger.LogDebug(
			"submit_source path={Path} dept={DeptId} classification={Classification}",
			filePath, deptId, effectiveClassification);

		// Step 1 — upload (creates the sources row).
		var uploadJson = await _api.UploadFileAsync(
				filePath,
				deptId,
				effectiveClassification,
				string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
				effectiveContentType,
				contributorOid,
				CancellationToken.None)
			.ConfigureAwait(false);

		var uploadNode = TryParse(uploadJson);
		if (uploadNode is null
			|| uploadNode is not JsonObject uploadObj
			|| !TryGetGuidField(uploadObj, "sourceId", out var newSourceId)
			|| newSourceId == Guid.Empty)
		{
			// Upload didn't produce a sourceId — return what the API said
			// so the AI client surfaces the underlying problem.
			return JsonSerializer.Serialize(new
			{
				upload = uploadNode,
				ingest = (object?)null,
				workstation = WorkstationPayload(),
			});
		}

		var blobUri = uploadObj["blobUri"]?.GetValue<string>();

		// Step 2 — enqueue ingest (worker will canonicalize + chunk + embed).
		string? ingestJson = null;
		JsonNode? ingestNode = null;
		if (!string.IsNullOrWhiteSpace(blobUri))
		{
			ingestJson = await _api.EnqueueIngestAsync(
					blobUri,
					effectiveContentType,
					fileName,
					correlationId: null,
					sourceId: newSourceId,
					CancellationToken.None)
				.ConfigureAwait(false);
			ingestNode = TryParse(ingestJson);
		}
		else
		{
			ingestNode = JsonNode.Parse("""{ "error": "Upload response did not include blobUri." }""");
		}

		return JsonSerializer.Serialize(new
		{
			upload = uploadNode,
			ingest = ingestNode,
			workstation = WorkstationPayload(),
		});
	}

	private static JsonNode? TryParse(string raw)
	{
		try
		{
			return JsonNode.Parse(raw);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static bool TryGetGuidField(JsonObject obj, string field, out Guid value)
	{
		value = Guid.Empty;
		if (!obj.TryGetPropertyValue(field, out var node) || node is null)
		{
			return false;
		}

		if (node is JsonValue jv && jv.TryGetValue<string>(out var raw))
		{
			return Guid.TryParse(raw, CultureInfo.InvariantCulture, out value);
		}

		return false;
	}

	[McpServerTool(Name = "get_source")]
	[Description("Fetch a source record and metadata by id via GET /api/sources/{id}. Returns {found:false} when the source is missing or hidden by RLS — by design the two cases are indistinguishable so callers cannot probe for hidden rows.")]
	public async Task<string> GetSource(string sourceId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
		if (!Guid.TryParse(sourceId, out var id))
		{
			return JsonSerializer.Serialize(new
			{
				error = "sourceId must be a GUID.",
				workstation = WorkstationPayload(),
			});
		}

		_logger.LogDebug("get_source id={Id} authenticated={Auth}", id, _workstation.HasBearerToken);
		var payload = await _api.GetSourceAsync(id, CancellationToken.None).ConfigureAwait(false);
		return AppendWorkstationFooter(payload);
	}

	[McpServerTool(Name = "list_departments")]
	[Description("List active departments visible to the caller via GET /api/departments. RLS limits the set to the caller's authorized departments per ADR 0005.")]
	public async Task<string> ListDepartments()
	{
		_logger.LogDebug("list_departments authenticated={Auth}", _workstation.HasBearerToken);
		var payload = await _api.ListDepartmentsAsync(CancellationToken.None).ConfigureAwait(false);
		return AppendWorkstationFooter(payload);
	}

	[McpServerTool(Name = "list_recent_changes")]
	[Description("List the most recent ingest / source / audit events visible to the caller via GET /api/audit/recent. Useful for 'what's been added to the corpus this week?' queries. Default 25, clamped to [1, 100]. RLS on audit_events scopes the visible set to admin or librarian-of-target-dept per ADR 0010.")]
	public async Task<string> ListRecentChanges(int limit = 25)
	{
		_logger.LogDebug("list_recent_changes limit={Limit} authenticated={Auth}", limit, _workstation.HasBearerToken);
		var payload = await _api.ListRecentChangesAsync(limit, CancellationToken.None).ConfigureAwait(false);
		return AppendWorkstationFooter(payload);
	}

	private string AppendWorkstationFooter(string apiJsonPayload)
	{
		try
		{
			var apiNode = JsonNode.Parse(apiJsonPayload) ?? new JsonObject();
			var wrapper = new JsonObject
			{
				["api"] = apiNode,
				["workstation"] = JsonSerializer.SerializeToNode(WorkstationPayload()),
			};
			return wrapper.ToJsonString();
		}
		catch (JsonException)
		{
			return JsonSerializer.Serialize(new
			{
				raw = apiJsonPayload,
				workstation = WorkstationPayload(),
			});
		}
	}

	private object WorkstationPayload()
	{
		return new
		{
			hasBearerToken = _workstation.HasBearerToken,
			claimsParsed = _workstation.ParsedClaims,
			entraObjectId = _workstation.EntraObjectId,
			directoryTenantId = _workstation.DirectoryTenantId,
			apiConfigured = _api.IsConfigured,
		};
	}
}
