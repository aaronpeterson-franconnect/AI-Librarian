namespace AiLibrarian.Mcp.Auth;

/// <summary>
/// Bound from the <c>Mcp:Auth</c> configuration section, with
/// <c>AILIB_*</c> env-var overrides resolved by
/// <see cref="BearerTokenProviderServiceCollectionExtensions"/>. When
/// every required field is populated, the MCP host registers the
/// MSAL silent-token provider; otherwise it falls back to reading the
/// static <c>AILIB_ACCESS_TOKEN</c> env var.
/// </summary>
public sealed class McpAuthOptions
{
	/// <summary>Configuration section name.</summary>
	public const string SectionName = "Mcp:Auth";

	/// <summary>Entra directory (tenant) id.</summary>
	public string TenantId { get; set; } = string.Empty;

	/// <summary>Public-client (desktop) application id; same registration the CLI uses.</summary>
	public string ClientId { get; set; } = string.Empty;

	/// <summary>Scopes to request (e.g. <c>api://&lt;api-app-id&gt;/.default</c>).</summary>
	public string[] ApiScopes { get; set; } = [];

	/// <summary>
	/// Directory holding the MSAL persistent cache. Defaults to the
	/// CLI's cache location so a single <c>ailib login</c> serves both
	/// the CLI and any spawned MCP host. Phase 1+ may add a
	/// per-host cache when stdio MCP is replaced with an HTTP transport.
	/// </summary>
	public string CacheDirectory { get; set; } = string.Empty;

	/// <summary>
	/// MSAL persistent cache file name. Matches the CLI default;
	/// changing one without the other will silently break SSO between
	/// <c>ailib login</c> and <c>ailib mcp</c>.
	/// </summary>
	public string CacheFileName { get; set; } = "msal_cache_v3.bin";

	/// <summary>True when every field MSAL silent acquisition needs is populated.</summary>
	public bool IsMsalConfigured =>
		!string.IsNullOrWhiteSpace(TenantId)
		&& !string.IsNullOrWhiteSpace(ClientId)
		&& ApiScopes.Length > 0;
}
