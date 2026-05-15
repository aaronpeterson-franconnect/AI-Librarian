using AiLibrarian.Domain;
using AiLibrarian.Domain.Users;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed <see cref="IUserAuthorizationWriter"/>. Pushes the
/// <see cref="RlsSessionContext.System"/> admin context onto every
/// connection so the writer can transcend per-caller RLS — necessary
/// because the Entra group-sync job is an out-of-band reconciliation
/// pass, not user-initiated.
///
/// <para>Audit responsibility lives one level up: the sync service
/// calls this writer and emits the <c>user_auth.granted</c> /
/// <c>user_auth.revoked</c> audit events itself, because the audit
/// rows need to carry per-grant metadata (group display name, role
/// transition direction) that's domain-specific and out of scope
/// for this storage adapter.</para>
/// </summary>
public sealed class PostgresUserAuthorizationWriter : IUserAuthorizationWriter
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresUserAuthorizationWriter> _logger;

	/// <summary>Creates the writer.</summary>
	public PostgresUserAuthorizationWriter(
		NpgsqlDataSource dataSource,
		ILogger<PostgresUserAuthorizationWriter> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<bool> GrantAsync(
		Guid userId,
		Guid? departmentId,
		Role role,
		string sourceGroupId,
		CancellationToken cancellationToken = default)
	{
		if (userId == Guid.Empty)
		{
			throw new ArgumentException("userId must be non-empty.", nameof(userId));
		}

		ArgumentException.ThrowIfNullOrWhiteSpace(sourceGroupId);

		// Schema invariant -- Admin must be system-wide, non-Admin must
		// have a department. Surface a clean argument exception instead
		// of letting the chk_user_auth_admin_no_dept constraint raise.
		if (role == Role.Admin && departmentId.HasValue)
		{
			throw new ArgumentException("Admin role must be system-wide (departmentId = null).");
		}

		if (role != Role.Admin && !departmentId.HasValue)
		{
			throw new ArgumentException($"Role {role} requires a non-null departmentId.");
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Two unique indices on user_authorizations:
		//   - ux_user_auth_admin       (user_id, role) WHERE department_id IS NULL
		//   - ux_user_auth_dept_role   (user_id, department_id, role) WHERE department_id IS NOT NULL
		// Pick the right ON CONFLICT branch by departmentId presence.
		var conflictTarget = departmentId.HasValue
			? "(user_id, department_id, role) WHERE department_id IS NOT NULL"
			: "(user_id, role) WHERE department_id IS NULL";

		var sql = $"""
			INSERT INTO user_authorizations (user_id, department_id, role, source_group_id)
			VALUES (@user_id, @dept_id, @role, @group)
			ON CONFLICT {conflictTarget} DO UPDATE SET source_group_id = EXCLUDED.source_group_id
			RETURNING (xmax = 0) AS is_insert
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("user_id", userId);
		cmd.Parameters.AddWithValue("dept_id", (object?)departmentId ?? DBNull.Value);
		cmd.Parameters.AddWithValue("role", role.ToString());
		cmd.Parameters.AddWithValue("group", sourceGroupId);

		bool isInsert = false;
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				// xmax = 0 distinguishes a fresh insert from an upsert
				// hit -- the trick from the Postgres docs.
				isInsert = reader.GetBoolean(0);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (isInsert)
		{
			_logger.LogInformation(
				"Granted role={Role} user={UserId} dept={DepartmentId} source_group={SourceGroup}",
				role,
				userId,
				departmentId,
				sourceGroupId);
		}

		return isInsert;
	}

	/// <inheritdoc />
	public async Task<int> ReconcileAsync(
		string sourceGroupId,
		IReadOnlyCollection<Guid> keepUserIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceGroupId);
		ArgumentNullException.ThrowIfNull(keepUserIds);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// keep = current Entra membership for this group. Anyone whose
		// row exists with this source_group_id but is NOT in keep gets
		// revoked. = ANY (@keep::uuid[]) handles the empty-array case
		// cleanly (matches nothing, so all rows for the group are deleted).
		const string sql = """
			DELETE FROM user_authorizations
			WHERE source_group_id = @group
			  AND NOT (user_id = ANY (@keep::uuid[]))
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("group", sourceGroupId);
		cmd.Parameters.AddWithValue("keep", keepUserIds.ToArray());

		var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (deleted > 0)
		{
			_logger.LogInformation(
				"Reconciled source_group={SourceGroup}: revoked {Deleted} grant(s) (keep count={KeepCount}).",
				sourceGroupId,
				deleted,
				keepUserIds.Count);
		}

		return deleted;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<UserAuthorization>> ListBySourceGroupAsync(
		string sourceGroupId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceGroupId);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT user_id, department_id, role, source_group_id, granted_at
			FROM user_authorizations
			WHERE source_group_id = @group
			""";

		var results = new List<UserAuthorization>();
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("group", sourceGroupId);

		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				if (!Enum.TryParse<Role>(reader.GetString(2), ignoreCase: false, out var role))
				{
					continue;
				}

				results.Add(new UserAuthorization(
					UserId: reader.GetGuid(0),
					DepartmentId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
					Role: role,
					SourceGroupId: reader.GetString(3),
					GrantedAt: reader.GetFieldValue<DateTimeOffset>(4)));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return results;
	}
}
