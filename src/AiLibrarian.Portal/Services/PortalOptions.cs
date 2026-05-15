namespace AiLibrarian.Portal.Services;

/// <summary>
/// Phase 1 portal configuration — operator defaults filled into the
/// upload form so engineers don't have to type a department GUID
/// every time. Keep this minimal; the Phase 2 librarian portal will
/// replace the form fields with a department/classification picker
/// driven by the user's Entra group memberships.
/// </summary>
public sealed class PortalOptions
{
	/// <summary>Configuration section name.</summary>
	public const string SectionName = "Portal";

	/// <summary>Default department id pre-filled on the upload form.</summary>
	public string DefaultDepartmentId { get; set; } = string.Empty;

	/// <summary>Default classification pre-filled on the upload form.</summary>
	public string DefaultClassification { get; set; } = "Internal";

	/// <summary>
	/// Optional contributor id pre-filled on the upload form when the
	/// portal is running without Entra (the dev escape hatch). In any
	/// Entra-configured environment, leave this blank -- the API will
	/// pull the OID claim instead.
	/// </summary>
	public string DefaultContributorId { get; set; } = string.Empty;

	/// <summary>
	/// Dev-only roster of pretend "guest" identities the upload form can
	/// choose from instead of asking the operator to type a GUID. Each
	/// entry must reference a real <c>users.id</c> seeded into the
	/// database (pilot seed inserts Pilot Engineer at
	/// <c>22222222-2222-2222-2222-222222222222</c>). Rendered as a
	/// dropdown only when the host environment is Development; ignored
	/// in production. Goes away entirely once Portal Entra sign-in
	/// lands and the API resolves the contributor from the <c>oid</c>
	/// claim.
	/// </summary>
	public List<DevContributor> DevContributors { get; set; } = new();
}

/// <summary>
/// One row in <see cref="PortalOptions.DevContributors"/>. Bound from
/// <c>Portal:DevContributors</c> in appsettings or
/// <c>Portal__DevContributors__N__Id</c> / <c>__DisplayName</c> env vars.
/// </summary>
public sealed class DevContributor
{
	/// <summary>Real <c>users.id</c> in the database; sent as the form's <c>contributorId</c>.</summary>
	public string Id { get; set; } = string.Empty;

	/// <summary>Friendly label rendered in the dropdown.</summary>
	public string DisplayName { get; set; } = string.Empty;
}
