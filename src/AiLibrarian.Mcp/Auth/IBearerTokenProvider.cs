namespace AiLibrarian.Mcp.Auth;

/// <summary>
/// Resolves the bearer token sent on each Librarian API call. Phase 1
/// has two implementations:
/// <list type="bullet">
///   <item><description><see cref="EnvironmentBearerTokenProvider"/> —
///   reads <c>AILIB_ACCESS_TOKEN</c> once at process start. The token
///   eventually expires; long-lived MCP sessions break.</description></item>
///   <item><description><see cref="MsalSilentBearerTokenProvider"/> —
///   uses MSAL silent acquisition against the shared <c>ailib login</c>
///   cache. Refresh tokens are exchanged transparently when the
///   access token nears expiry, so a stdio MCP host can stay open all
///   day without re-login.</description></item>
/// </list>
/// </summary>
public interface IBearerTokenProvider
{
	/// <summary>
	/// The current bearer token, or <see langword="null"/> when no
	/// token is available (no signed-in account, expired refresh
	/// token, or auth provider not configured). Callers omit the
	/// <c>Authorization</c> header in that case; the API responds 401
	/// and the MCP tool surface returns the typed error to the AI
	/// client.
	/// </summary>
	Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);
}
