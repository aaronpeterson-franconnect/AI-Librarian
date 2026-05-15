namespace AiLibrarian.Domain;

/// <summary>
/// A user's grant of a persona, optionally department-scoped and
/// optionally time-bounded — per ADR 0014.
/// </summary>
/// <param name="UserId">The user's Entra OID.</param>
/// <param name="PersonaId">The persona granted.</param>
/// <param name="DepartmentId">Optional scope; null means the persona
/// applies regardless of department context.</param>
/// <param name="GrantedAt">When the membership was granted.</param>
/// <param name="ExpiresAt">Optional expiry; null means open-ended.</param>
/// <param name="GrantedBy">User who issued the grant (Admin or department Librarian).</param>
public sealed record PersonaMembership(
	Guid UserId,
	Guid PersonaId,
	Guid? DepartmentId,
	DateTimeOffset GrantedAt,
	DateTimeOffset? ExpiresAt,
	Guid GrantedBy)
{
	/// <summary>True when the membership is currently in effect.</summary>
	public bool IsActiveAt(DateTimeOffset asOf)
		=> ExpiresAt is null || ExpiresAt > asOf;
}
