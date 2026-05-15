namespace AiLibrarian.Api.Portal;

/// <summary>
/// Configuration for the Phase 1 portal upload route. The
/// Engineering pilot operates without a department picker UI, so the
/// upload form falls back to <see cref="DefaultDepartmentId"/> when
/// the caller doesn't supply one — set this once per environment to
/// the pilot department id.
/// </summary>
public sealed class PortalOptions
{
	/// <summary>Configuration section name.</summary>
	public const string SectionName = "Portal";

	/// <summary>
	/// Department id assumed for portal uploads that don't include an
	/// explicit <c>departmentId</c> form field. Empty in source so the
	/// API surfaces a 400 rather than a silent default; operators set
	/// this in <c>appsettings.{Environment}.json</c> after creating
	/// the pilot department.
	/// </summary>
	public string DefaultDepartmentId { get; set; } = string.Empty;

	/// <summary>
	/// Default classification when the upload form leaves the field
	/// blank. Defaults to <c>Internal</c> per ADR 0011 — every
	/// authenticated employee can read it, but write authority stays
	/// gated by department + role.
	/// </summary>
	public string DefaultClassification { get; set; } = "Internal";

	/// <summary>
	/// Development-only escape hatch for a slim pilot that runs apps locally
	/// with Azure data-plane services but without Entra. When true and the
	/// host environment is Development, upload may use <c>contributorId</c>
	/// plus <see cref="DefaultDepartmentId"/> as the RLS write context.
	/// </summary>
	public bool DevelopmentRlsOverrideEnabled { get; set; }
}
