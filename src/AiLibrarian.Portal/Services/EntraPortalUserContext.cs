using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace AiLibrarian.Portal.Services;

/// <summary>
/// Claims-based <see cref="IPortalUserContext"/>. Active when
/// <c>AzureAd:ClientId</c> is configured; relies on
/// Microsoft.Identity.Web populating the
/// <see cref="HttpContext.User"/> with the standard OIDC claim set.
/// </summary>
internal sealed class EntraPortalUserContext : IPortalUserContext
{
	private const string OidClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

	private readonly IHttpContextAccessor _httpContextAccessor;

	public EntraPortalUserContext(IHttpContextAccessor httpContextAccessor)
	{
		_httpContextAccessor = httpContextAccessor;
	}

	public bool EntraEnabled => true;

	public bool IsAuthenticated => CurrentUser?.Identity?.IsAuthenticated == true;

	public string DisplayName
	{
		get
		{
			var user = CurrentUser;
			if (user is null)
			{
				return string.Empty;
			}

			// Prefer the friendly "name" claim, then preferred_username,
			// then any string identifier we can fall back to. The empty
			// string is the explicit "not signed in" signal.
			return user.FindFirstValue("name")
				?? user.FindFirstValue("preferred_username")
				?? user.Identity?.Name
				?? string.Empty;
		}
	}

	public string ContributorId
	{
		get
		{
			var user = CurrentUser;
			if (user is null)
			{
				return string.Empty;
			}

			// The OID claim is Entra's stable per-tenant identifier; the
			// API joins on this to find the users.id row. Both the
			// standard short name ("oid") and the schemas.microsoft.com
			// URI map to the same claim depending on token version.
			return user.FindFirstValue("oid")
				?? user.FindFirstValue(OidClaim)
				?? string.Empty;
		}
	}

	public IReadOnlyList<DevContributor> SelectableContributors { get; } = Array.Empty<DevContributor>();

	public void SelectContributor(string contributorId)
	{
		throw new InvalidOperationException(
			"Contributor selection is dev-only. In Entra mode the signed-in user is the contributor.");
	}

	private ClaimsPrincipal? CurrentUser => _httpContextAccessor.HttpContext?.User;
}
