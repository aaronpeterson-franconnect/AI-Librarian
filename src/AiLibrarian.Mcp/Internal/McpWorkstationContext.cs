using System.IdentityModel.Tokens.Jwt;

namespace AiLibrarian.Mcp.Internal;

/// <summary>
/// Bearer token supplied by <c>ailib mcp</c> via <c>AILIB_ACCESS_TOKEN</c>.
/// Claims are read without signature validation (Phase 1); API-side validation happens on HTTP calls later.
/// </summary>
public sealed record McpWorkstationContext(
	bool HasBearerToken,
	bool ParsedClaims,
	string? EntraObjectId,
	string? DirectoryTenantId)
{
	internal static McpWorkstationContext FromEnvironment()
	{
		var raw = Environment.GetEnvironmentVariable("AILIB_ACCESS_TOKEN");
		if (string.IsNullOrWhiteSpace(raw))
		{
			return new McpWorkstationContext(false, false, null, null);
		}

		var token = raw.Trim();
		try
		{
			var handler = new JwtSecurityTokenHandler();
			if (!handler.CanReadToken(token))
			{
				return new McpWorkstationContext(true, false, null, null);
			}

			var jwt = handler.ReadJwtToken(token);
			var oid = FindClaim(jwt, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier");
			var tid = FindClaim(jwt, "tid", "http://schemas.microsoft.com/identity/claims/tenantid");
			return new McpWorkstationContext(true, true, oid, tid);
		}
		catch
		{
			return new McpWorkstationContext(true, false, null, null);
		}
	}

	private static string? FindClaim(JwtSecurityToken jwt, params string[] types)
	{
		foreach (var t in types)
		{
			var c = jwt.Claims.FirstOrDefault(x => string.Equals(x.Type, t, StringComparison.OrdinalIgnoreCase));
			if (c is not null && !string.IsNullOrWhiteSpace(c.Value))
			{
				return c.Value;
			}
		}

		return null;
	}
}
