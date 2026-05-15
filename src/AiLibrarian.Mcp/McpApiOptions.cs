namespace AiLibrarian.Mcp;

/// <summary>AI Librarian HTTP API used by MCP tools (hybrid search, ingest enqueue).</summary>
public sealed class McpApiOptions
{
	public const string SectionName = "Api";

	/// <summary>Base URL of AiLibrarian.Api (no trailing slash), e.g. https://api.contoso.com</summary>
	public string BaseUrl { get; set; } = "";
}
