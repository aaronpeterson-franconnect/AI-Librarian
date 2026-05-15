namespace AiLibrarian.Mcp.Internal;

/// <summary>
/// Minimal extension → MIME type lookup for the formats Phase 1
/// ingest understands. Lives here rather than depending on
/// <c>Microsoft.AspNetCore.StaticFiles</c> so the MCP host stays free
/// of ASP.NET — it ships as a stdio child process. Falls back to
/// <c>application/octet-stream</c> for unknown extensions; the
/// registered Skill will then refuse to canonicalize and the worker
/// dead-letters the message with a clear reason rather than silently
/// guessing.
/// </summary>
public static class MimeTypeMap
{
	private static readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase)
	{
		[".md"] = "text/markdown",
		[".markdown"] = "text/markdown",
		[".txt"] = "text/plain",
		[".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
		[".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
		[".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
		[".pdf"] = "application/pdf",
		[".html"] = "text/html",
		[".htm"] = "text/html",
		[".json"] = "application/json",
		[".csv"] = "text/csv",
	};

	/// <summary>
	/// Look up the registered MIME type for a path's extension.
	/// Returns <c>application/octet-stream</c> when no mapping
	/// exists. Always non-null so callers can append directly to
	/// HTTP headers without a separate null-check.
	/// </summary>
	public static string ResolveByPath(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		var ext = Path.GetExtension(path);
		if (string.IsNullOrEmpty(ext))
		{
			return "application/octet-stream";
		}

		return _byExtension.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
	}
}
