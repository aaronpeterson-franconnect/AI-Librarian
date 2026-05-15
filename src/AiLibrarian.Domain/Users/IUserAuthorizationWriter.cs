namespace AiLibrarian.Domain.Users;

/// <summary>
/// Write side of <c>user_authorizations</c>. Distinct from
/// <see cref="IUserDirectory"/> (which is per-caller read + self-write)
/// because role grants are administrative — the implementation pushes
/// a system-admin RLS context so it can write rows on any user's behalf.
///
/// <para>Per ADR 0005, this is the surface the Entra group-sync job
/// writes through. Both the periodic background job and the on-demand
/// <c>POST /api/admin/entra-sync</c> endpoint share the same writer.</para>
/// </summary>
public interface IUserAuthorizationWriter
{
	/// <summary>
	/// Upsert a (user, department, role) grant tagged with
	/// <paramref name="sourceGroupId"/>. Idempotent — calling twice with
	/// the same (user, department, role) is a no-op via the unique
	/// indices. Returns true when a new row was inserted, false when
	/// the existing row was already present.
	/// </summary>
	Task<bool> GrantAsync(
		Guid userId,
		Guid? departmentId,
		Role role,
		string sourceGroupId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Delete every <c>user_authorizations</c> row whose
	/// <c>source_group_id</c> equals <paramref name="sourceGroupId"/>
	/// AND whose <c>user_id</c> is NOT in <paramref name="keepUserIds"/>.
	/// Used by the group-sync reconciliation pass: pass the group's
	/// current Entra membership as <paramref name="keepUserIds"/> and
	/// the writer drops anyone who was removed from the group since
	/// last sync. Returns the number of rows deleted.
	/// </summary>
	Task<int> ReconcileAsync(
		string sourceGroupId,
		IReadOnlyCollection<Guid> keepUserIds,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// List every <c>user_authorizations</c> row tagged with
	/// <paramref name="sourceGroupId"/>. Used by the group-sync job to
	/// compute the diff against current Entra membership.
	/// </summary>
	Task<IReadOnlyList<UserAuthorization>> ListBySourceGroupAsync(
		string sourceGroupId,
		CancellationToken cancellationToken = default);
}
