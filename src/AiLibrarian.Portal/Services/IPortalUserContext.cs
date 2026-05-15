namespace AiLibrarian.Portal.Services;

/// <summary>
/// Resolves "who is the contributor for the current request" without
/// the calling component having to know whether Entra sign-in is on.
/// Two implementations:
/// <list type="bullet">
///   <item><see cref="EntraPortalUserContext"/> — reads the OID claim
///         from the signed-in user, used when <c>AzureAd:ClientId</c>
///         is configured.</item>
///   <item><see cref="DevPortalUserContext"/> — falls back to the
///         per-environment <see cref="PortalOptions.DevContributors"/>
///         roster (Development only) or the configured
///         <see cref="PortalOptions.DefaultContributorId"/>.</item>
/// </list>
/// </summary>
public interface IPortalUserContext
{
	/// <summary>True when sign-in is required and a user is authenticated.</summary>
	bool IsAuthenticated { get; }

	/// <summary>
	/// True when the Portal is running with Entra sign-in. False when the
	/// dev-mode fallback path is active.
	/// </summary>
	bool EntraEnabled { get; }

	/// <summary>
	/// The display name for the current contributor — either the Entra
	/// user's name claim or the DevContributor's <c>DisplayName</c>.
	/// Empty when no contributor is selected.
	/// </summary>
	string DisplayName { get; }

	/// <summary>
	/// The contributor's <c>users.id</c> in the database. In Entra mode
	/// this comes from the <c>oid</c> claim (provisioned-by-API assumption);
	/// in dev mode it's the currently-selected DevContributor.
	/// Empty when no contributor is selected.
	/// </summary>
	string ContributorId { get; }

	/// <summary>
	/// Roster of selectable contributors. Populated only in dev-mode;
	/// empty in Entra mode (signed-in user is the only contributor).
	/// </summary>
	IReadOnlyList<DevContributor> SelectableContributors { get; }

	/// <summary>
	/// In dev mode, swap the currently-selected contributor. Throws in
	/// Entra mode (the signed-in user is not swappable).
	/// </summary>
	void SelectContributor(string contributorId);
}
