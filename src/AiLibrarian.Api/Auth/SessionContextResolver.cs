using System.Security.Claims;

using AiLibrarian.Domain.Users;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

namespace AiLibrarian.Api.Auth;

/// <summary>
/// Builds the per-request <see cref="RlsSessionContext"/>. Combines two
/// signals:
/// <list type="number">
///   <item>Claims from the <see cref="ClaimsPrincipal"/> — OID, idtyp,
///         acct, persona — already validated by the JWT bearer middleware.</item>
///   <item><see cref="IUserDirectory"/> — JIT-provisions the
///         <c>users</c> row and reads <c>user_authorizations</c> so the
///         RLS context carries real role data instead of empty arrays.</item>
/// </list>
///
/// <para>Scoped service: each request gets its own resolver, which in
/// turn drives the per-request projection cache in
/// <see cref="IUserDirectory"/>.</para>
/// </summary>
public interface ISessionContextResolver
{
	/// <summary>Resolve the full session context for the current request.</summary>
	Task<SessionContextBuilder.SessionContextDto> ResolveAsync(
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ISessionContextResolver"/>.</summary>
internal sealed class SessionContextResolver : ISessionContextResolver
{
	private readonly IUserDirectory _directory;
	private readonly ILogger<SessionContextResolver> _logger;

	public SessionContextResolver(IUserDirectory directory, ILogger<SessionContextResolver> logger)
	{
		_directory = directory;
		_logger = logger;
	}

	public async Task<SessionContextBuilder.SessionContextDto> ResolveAsync(
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(user);

		// Start from the claims-only projection. This handles all the
		// claims-derived fields uniformly (oid, idtyp, acct, persona) and
		// also covers the dev-mode no-Entra path where no directory lookup
		// is needed (the resolver still runs but with empty department
		// arrays via the early return below).
		var fromClaims = SessionContextBuilder.FromClaims(user);

		if (!fromClaims.IsAuthenticated || fromClaims.UserId == Guid.Empty)
		{
			// Anonymous or missing OID -- skip the directory; RLS will see
			// app.is_authenticated=false and restrict reads to Public.
			return fromClaims;
		}

		// JIT provision the row. The bearer claims are authoritative for
		// is_employee + display + email; we want every sign-in to refresh
		// those so the database mirrors current Entra state.
		var email = user.FindFirstValue("preferred_username")
			?? user.FindFirstValue("upn")
			?? user.FindFirstValue(ClaimTypes.Email);
		var name = user.FindFirstValue("name")
			?? user.Identity?.Name;

		try
		{
			await _directory.EnsureUserAsync(
				fromClaims.UserId,
				email,
				name,
				fromClaims.IsEmployee,
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// JIT failure -- log loudly but don't 500 the request. The
			// resolver will fall through to claims-only data; the user
			// gets RLS-restricted access (Public sources, plus anything
			// where existing user_authorizations happen to be present).
			_logger.LogError(ex,
				"JIT user provisioning failed for oid={Oid}; falling back to claims-only session context.",
				fromClaims.UserId);
			return fromClaims;
		}

		var projection = await _directory
			.GetProjectionAsync(fromClaims.UserId, cancellationToken)
			.ConfigureAwait(false);

		if (projection is null)
		{
			// Provisioning succeeded but the read came back empty (race
			// or RLS suppression). Same fallback as the catch above.
			_logger.LogWarning(
				"User projection null after EnsureUser for oid={Oid}; session context will be claims-only.",
				fromClaims.UserId);
			return fromClaims;
		}

		return fromClaims with
		{
			IsEmployee = projection.User.IsEmployee,
			HomeDepartmentIds = projection.HomeDepartmentIds,
			ContributorDepartmentIds = projection.ContributorDepartmentIds,
			ReviewerDepartmentIds = projection.ReviewerDepartmentIds,
			LibrarianDepartmentIds = projection.LibrarianDepartmentIds,
			IsAdmin = projection.IsAdmin,
		};
	}
}
