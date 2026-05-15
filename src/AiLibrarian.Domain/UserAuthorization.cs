namespace AiLibrarian.Domain;

/// <summary>
/// A grant of a <see cref="Role"/> to a user within a specific
/// department (or system-wide for <see cref="Role.Admin"/>).
/// Materialized from Microsoft Entra group membership per ADR 0005.
/// </summary>
/// <param name="UserId">The user's Entra OID.</param>
/// <param name="DepartmentId">Target department, or null for system-wide
/// (only valid with <see cref="Role.Admin"/>).</param>
/// <param name="Role">The granted role.</param>
/// <param name="SourceGroupId">The Entra group that drove this grant (audit anchor).</param>
/// <param name="GrantedAt">When the grant was first observed.</param>
public sealed record UserAuthorization(
	Guid UserId,
	Guid? DepartmentId,
	Role Role,
	string SourceGroupId,
	DateTimeOffset GrantedAt);
