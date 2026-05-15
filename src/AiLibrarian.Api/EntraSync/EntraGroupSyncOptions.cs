using AiLibrarian.Domain;

namespace AiLibrarian.Api.EntraSync;

/// <summary>
/// Configuration for the Entra group-sync job. Bound from the
/// <c>EntraSync</c> configuration section.
///
/// <para>Empty <see cref="GroupMappings"/> means the job is disabled
/// (the hosted service no-ops, the admin endpoint returns 400 with a
/// "not configured" detail). This is the default in dev so the job
/// doesn't try to talk to Graph without credentials.</para>
/// </summary>
public sealed class EntraGroupSyncOptions
{
	/// <summary>Configuration section name.</summary>
	public const string SectionName = "EntraSync";

	/// <summary>
	/// Master switch. When false (default), the hosted service doesn't
	/// start the periodic timer and the admin endpoint returns 400 —
	/// even if other fields are filled in. Lets ops disable the job
	/// without dropping config.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// Microsoft tenant id. The Graph token is acquired against this
	/// tenant's <c>v2.0</c> token endpoint.
	/// </summary>
	public string TenantId { get; set; } = string.Empty;

	/// <summary>The API's own app-registration client id (the one with <c>GroupMember.Read.All</c> Graph permission).</summary>
	public string ClientId { get; set; } = string.Empty;

	/// <summary>The app-registration client secret. Prefer Key Vault references in production.</summary>
	public string ClientSecret { get; set; } = string.Empty;

	/// <summary>Periodic schedule. Defaults to 15 minutes.</summary>
	public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

	/// <summary>
	/// HTTP timeout per Graph request. Defaults to 30 seconds; raise
	/// for large tenants where group listing pages can take longer.
	/// </summary>
	public TimeSpan GraphRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Group → role mappings. Each row says "members of Entra group X
	/// receive role Y in department Z." Admin grants use a null
	/// <see cref="EntraGroupMapping.DepartmentId"/>.
	/// </summary>
	public List<EntraGroupMapping> GroupMappings { get; set; } = new();
}

/// <summary>
/// One operator-declared mapping. The group's display name is captured
/// for audit only (the object id is the stable identifier).
/// </summary>
public sealed class EntraGroupMapping
{
	/// <summary>Entra group object id (GUID). Stable across renames.</summary>
	public string GroupObjectId { get; set; } = string.Empty;

	/// <summary>Friendly label captured for audit; e.g. "Engineering Librarians". Optional.</summary>
	public string? DisplayLabel { get; set; }

	/// <summary>Target role to grant to every member of the group.</summary>
	public Role Role { get; set; } = Role.Reader;

	/// <summary>
	/// Target department id. Required for non-Admin roles; must be
	/// empty for Admin grants. The sync service validates upfront and
	/// skips invalid mappings with a warning rather than failing the
	/// whole run.
	/// </summary>
	public string DepartmentId { get; set; } = string.Empty;
}
