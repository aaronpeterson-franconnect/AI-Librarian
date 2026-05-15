namespace AiLibrarian.Mcp.Auth;

/// <summary>
/// Reads the bearer token from the <c>AILIB_ACCESS_TOKEN</c>
/// environment variable on every call. The env-var value is captured
/// at process start by <c>ailib mcp</c>; this provider re-reads on
/// each request so a parent process that updates the env (rare, but
/// possible on Linux via /proc/self/environ writes) is honored. Used
/// as the fallback when <see cref="MsalSilentBearerTokenProvider"/>
/// can't be configured.
/// </summary>
public sealed class EnvironmentBearerTokenProvider : IBearerTokenProvider
{
	/// <summary>Environment variable name carrying the bearer token.</summary>
	public const string EnvironmentVariableName = "AILIB_ACCESS_TOKEN";

	/// <inheritdoc />
	public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
	{
		var raw = Environment.GetEnvironmentVariable(EnvironmentVariableName);
		var trimmed = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
		return Task.FromResult(trimmed);
	}
}
