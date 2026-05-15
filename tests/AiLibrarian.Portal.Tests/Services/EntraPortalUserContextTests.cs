using System.Security.Claims;

using AiLibrarian.Portal.Services;

using Microsoft.AspNetCore.Http;

namespace AiLibrarian.Portal.Tests.Services;

/// <summary>
/// Pins the claims-based contributor resolution. Two Entra access-token
/// flavors exist in the wild — v1 tokens use schemas.microsoft.com URIs
/// for claim types, v2 tokens use short names — and the user context
/// must handle both. These tests assert that without standing up a real
/// IdP.
/// </summary>
public sealed class EntraPortalUserContextTests
{
	[Fact]
	public void Authenticated_User_With_Oid_And_Name_Claims_Resolves()
	{
		var user = MakePrincipal(authenticated: true,
			("oid", "11111111-2222-3333-4444-555555555555"),
			("name", "Aaron Peterson"));

		var ctx = new EntraPortalUserContext(AccessorReturning(user));

		ctx.EntraEnabled.Should().BeTrue();
		ctx.IsAuthenticated.Should().BeTrue();
		ctx.ContributorId.Should().Be("11111111-2222-3333-4444-555555555555");
		ctx.DisplayName.Should().Be("Aaron Peterson");
	}

	[Fact]
	public void Schemas_Uri_Oid_Claim_Format_Also_Resolves()
	{
		// v1 access tokens carry the OID under the longer URI form.
		var user = MakePrincipal(authenticated: true,
			("http://schemas.microsoft.com/identity/claims/objectidentifier", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
			("preferred_username", "ap@example.com"));

		var ctx = new EntraPortalUserContext(AccessorReturning(user));

		ctx.ContributorId.Should().Be("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
		ctx.DisplayName.Should().Be("ap@example.com");
	}

	[Fact]
	public void Unauthenticated_Principal_Returns_Empty_And_Not_Authenticated()
	{
		var user = MakePrincipal(authenticated: false);

		var ctx = new EntraPortalUserContext(AccessorReturning(user));

		ctx.IsAuthenticated.Should().BeFalse();
		ctx.ContributorId.Should().Be(string.Empty);
		ctx.DisplayName.Should().Be(string.Empty);
	}

	[Fact]
	public void Missing_HttpContext_Degrades_To_Empty_Without_Throwing()
	{
		var accessor = new HttpContextAccessor { HttpContext = null };
		var ctx = new EntraPortalUserContext(accessor);

		ctx.IsAuthenticated.Should().BeFalse();
		ctx.ContributorId.Should().Be(string.Empty);
		ctx.DisplayName.Should().Be(string.Empty);
	}

	[Fact]
	public void SelectableContributors_Is_Empty_In_Entra_Mode()
	{
		var ctx = new EntraPortalUserContext(AccessorReturning(MakePrincipal(authenticated: true)));
		ctx.SelectableContributors.Should().BeEmpty();
	}

	[Fact]
	public void SelectContributor_Throws_In_Entra_Mode()
	{
		var ctx = new EntraPortalUserContext(AccessorReturning(MakePrincipal(authenticated: true)));
		var act = () => ctx.SelectContributor(Guid.NewGuid().ToString("D"));
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*dev-only*");
	}

	private static ClaimsPrincipal MakePrincipal(bool authenticated, params (string Type, string Value)[] claims)
	{
		var identity = authenticated
			? new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Test")
			: new ClaimsIdentity();
		return new ClaimsPrincipal(identity);
	}

	private static HttpContextAccessor AccessorReturning(ClaimsPrincipal user)
	{
		var httpContext = new DefaultHttpContext { User = user };
		return new HttpContextAccessor { HttpContext = httpContext };
	}
}
