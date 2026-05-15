using System.Security.Claims;

using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Api.Auth;

/// <summary>
/// Builds an <see cref="RlsSessionContext"/> from the current
/// <see cref="ClaimsPrincipal"/> per ADR 0005. Phase 0 stub:
/// pulls only the user OID and authentication state from the token;
/// the department-and-role lists are populated from the
/// <c>user_authorizations</c> table in Phase 0 + Phase 1 via a
/// follow-up service. The persona id is read from the
/// <c>Default-Persona</c> claim if present, per the persona-selector
/// contract in ADR 0015.
/// </summary>
public static class SessionContextBuilder
{
	/// <summary>
	/// Project a <see cref="ClaimsPrincipal"/> into a session-context DTO
	/// for /me responses. The DTO is the JSON-friendly shape; an
	/// <see cref="RlsSessionContext"/> is built off the same data when
	/// pushing onto a Postgres connection.
	/// </summary>
	public static SessionContextDto FromClaims(ClaimsPrincipal user)
	{
		ArgumentNullException.ThrowIfNull(user);

		var oidClaim = user.FindFirstValue("oid")
			?? user.FindFirstValue(ClaimTypes.NameIdentifier);

		var userId = Guid.TryParse(oidClaim, out var parsed)
			? parsed
			: Guid.Empty;

		var isAuthenticated = user.Identity?.IsAuthenticated ?? false;

		// idtyp claim distinguishes "user" tokens from app-only "app" tokens;
		// "Member" idp claim distinguishes employees from B2B guests.
		var isEmployee = isAuthenticated
			&& string.Equals(user.FindFirstValue("idtyp"), "user", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(user.FindFirstValue("acct"), "1", StringComparison.OrdinalIgnoreCase);

		var personaIdClaim = user.FindFirstValue("Default-Persona");
		var personaId = Guid.TryParse(personaIdClaim, out var p) ? (Guid?)p : null;

		return new SessionContextDto(
			UserId: userId,
			IsAuthenticated: isAuthenticated,
			IsEmployee: isEmployee,
			HomeDepartmentIds: Array.Empty<Guid>(),
			ContributorDepartmentIds: Array.Empty<Guid>(),
			ReviewerDepartmentIds: Array.Empty<Guid>(),
			LibrarianDepartmentIds: Array.Empty<Guid>(),
			IsAdmin: false,
			PersonaId: personaId);
	}

	/// <summary>JSON-friendly projection of <see cref="RlsSessionContext"/>.</summary>
	public sealed record SessionContextDto(
		Guid UserId,
		bool IsAuthenticated,
		bool IsEmployee,
		IReadOnlyCollection<Guid> HomeDepartmentIds,
		IReadOnlyCollection<Guid> ContributorDepartmentIds,
		IReadOnlyCollection<Guid> ReviewerDepartmentIds,
		IReadOnlyCollection<Guid> LibrarianDepartmentIds,
		bool IsAdmin,
		Guid? PersonaId)
	{
		/// <summary>Project to the immutable Infrastructure context shape.</summary>
		public RlsSessionContext ToContext() => new(
			UserId: UserId,
			IsAuthenticated: IsAuthenticated,
			IsEmployee: IsEmployee,
			HomeDepartmentIds: HomeDepartmentIds,
			ContributorDepartmentIds: ContributorDepartmentIds,
			ReviewerDepartmentIds: ReviewerDepartmentIds,
			LibrarianDepartmentIds: LibrarianDepartmentIds,
			IsAdmin: IsAdmin,
			PersonaId: PersonaId);
	}
}
