using AiLibrarian.Domain;

namespace AiLibrarian.Infrastructure.Rls;

/// <summary>
/// The set of session variables the API pushes onto every Postgres
/// connection per ADR 0005. The values are set with <c>SET LOCAL</c>
/// so they apply only to the current transaction.
///
/// <para>
/// <b>Persona is not part of the visibility predicate</b> per the
/// ADR 0005 amendment. <see cref="PersonaId"/> is included here for
/// retrieval ranking, synthesis style, and persona-action authority
/// only — RLS read predicates do not consult it.
/// </para>
/// </summary>
/// <param name="UserId">The user's Entra OID.</param>
/// <param name="IsAuthenticated">True for any signed-in user (employee or guest).</param>
/// <param name="IsEmployee">True only for tenant employees;
/// the <c>Internal</c>-content read access requires this flag.</param>
/// <param name="HomeDepartmentIds">Departments where the user holds any role.</param>
/// <param name="ContributorDepartmentIds">Departments where the user is Contributor or higher.</param>
/// <param name="ReviewerDepartmentIds">Departments where the user is Reviewer or higher.</param>
/// <param name="LibrarianDepartmentIds">Departments where the user is Librarian or higher.</param>
/// <param name="IsAdmin">System-wide Admin flag.</param>
/// <param name="PersonaId">Primary persona for the session, or null when the
/// user has no persona memberships (neutral retrieval).</param>
public sealed record RlsSessionContext(
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
	/// <summary>
	/// Construct an anonymous (unauthenticated) context. Reads are
	/// limited to <see cref="Classification.Public"/> sources by RLS.
	/// </summary>
	public static RlsSessionContext Anonymous() => new(
		UserId: Guid.Empty,
		IsAuthenticated: false,
		IsEmployee: false,
		HomeDepartmentIds: Array.Empty<Guid>(),
		ContributorDepartmentIds: Array.Empty<Guid>(),
		ReviewerDepartmentIds: Array.Empty<Guid>(),
		LibrarianDepartmentIds: Array.Empty<Guid>(),
		IsAdmin: false,
		PersonaId: null);

	/// <summary>
	/// Construct a system context for autonomous infrastructure work
	/// (audit-ledger inserts, startup probes, partition maintenance).
	/// Uses the seeded <c>AuditConstants.SystemUserId</c> sentinel,
	/// <see cref="IsAuthenticated"/> = <see langword="true"/> to satisfy
	/// the <c>audit_events</c> insert predicate, and <see cref="IsAdmin"/>
	/// = <see langword="true"/> so the system path is never blocked by
	/// department-scoped predicates. <see cref="IsEmployee"/> remains
	/// <see langword="false"/> — the system principal is not a person and
	/// must not unlock company-wide <c>Internal</c> reads.
	/// </summary>
	public static RlsSessionContext System() => new(
		UserId: new Guid("00000000-0000-0000-0000-00000000ffff"),
		IsAuthenticated: true,
		IsEmployee: false,
		HomeDepartmentIds: Array.Empty<Guid>(),
		ContributorDepartmentIds: Array.Empty<Guid>(),
		ReviewerDepartmentIds: Array.Empty<Guid>(),
		LibrarianDepartmentIds: Array.Empty<Guid>(),
		IsAdmin: true,
		PersonaId: null);
}
