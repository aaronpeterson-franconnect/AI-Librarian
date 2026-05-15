namespace AiLibrarian.Cli.Configuration;

/// <summary>Entra settings for the public (desktop) client used with device-code flow.</summary>
public sealed class CliAuthOptions
{
	public const string SectionPath = "Cli:Auth";

	/// <summary>Directory (tenant) id.</summary>
	public string TenantId { get; set; } = string.Empty;

	/// <summary>Public client application id (desktop / native registration).</summary>
	public string ClientId { get; set; } = string.Empty;

	/// <summary>Scopes to request (e.g. <c>api://&lt;api-app-id&gt;/.default</c> or <c>api://.../access_as_user</c>).</summary>
	public string[] ApiScopes { get; set; } = [];
}
