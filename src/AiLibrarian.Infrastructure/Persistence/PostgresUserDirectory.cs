using AiLibrarian.Domain;
using AiLibrarian.Domain.Users;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed <see cref="IUserDirectory"/>. Two responsibilities:
/// <list type="bullet">
///   <item>JIT upsert the <c>users</c> row when a new OID is first seen
///         (the new <c>p_users_self_insert</c> / <c>p_users_self_update</c>
///         policies from <c>0101-users-self-provisioning.sql</c> let this
///         run under the caller's own RLS session).</item>
///   <item>Read the user + their authorizations for session-context
///         building. Uses self-read predicates so no admin context is
///         needed.</item>
/// </list>
///
/// <para>Per-instance scoped cache prevents re-hitting Postgres within
/// the same request scope — a handful of routes call
/// <c>SessionContextResolver.ResolveAsync</c> followed by a retrieval
/// call that uses the same context; caching here avoids a second
/// round-trip.</para>
/// </summary>
public sealed class PostgresUserDirectory : IUserDirectory
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresUserDirectory> _logger;
	private readonly Dictionary<Guid, UserDirectoryProjection> _cache = new();
	private readonly object _cacheLock = new();

	/// <summary>Creates the directory.</summary>
	public PostgresUserDirectory(
		NpgsqlDataSource dataSource,
		ILogger<PostgresUserDirectory> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<UserRow> EnsureUserAsync(
		Guid oid,
		string? email,
		string? displayName,
		bool isEmployee,
		CancellationToken cancellationToken = default)
	{
		if (oid == Guid.Empty)
		{
			throw new ArgumentException("oid must be non-empty.", nameof(oid));
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Push a minimal RLS session so the self-insert / self-update
		// policy's `id = app_user_id()` predicate evaluates correctly.
		// We don't have the user's roles yet (that's the next query),
		// so the rest of the session stays empty. Self-insert / self-
		// update only consult app_user_id() + app_is_authenticated(),
		// so this is enough.
		var minimal = new RlsSessionContext(
			UserId: oid,
			IsAuthenticated: true,
			IsEmployee: isEmployee,
			HomeDepartmentIds: Array.Empty<Guid>(),
			ContributorDepartmentIds: Array.Empty<Guid>(),
			ReviewerDepartmentIds: Array.Empty<Guid>(),
			LibrarianDepartmentIds: Array.Empty<Guid>(),
			IsAdmin: false,
			PersonaId: null);
		await RlsSessionPusher.PushAsync(conn, minimal, cancellationToken).ConfigureAwait(false);

		// UPSERT. On insert, populate everything. On conflict, refresh the
		// fields we have signal about (email + display_name + is_employee).
		// is_employee can transition from false to true if a guest later
		// becomes a member, or the other way around -- the bearer is the
		// authority on every sign-in.
		const string sql = """
			INSERT INTO users (id, email, display_name, is_employee)
			VALUES (@id, NULLIF(@email, ''), NULLIF(@display_name, ''), @is_employee)
			ON CONFLICT (id) DO UPDATE SET
				email        = COALESCE(NULLIF(EXCLUDED.email, ''), users.email),
				display_name = COALESCE(NULLIF(EXCLUDED.display_name, ''), users.display_name),
				is_employee  = EXCLUDED.is_employee
			RETURNING id, email, display_name, is_employee, deactivated_at, created_at
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("id", oid);
		cmd.Parameters.AddWithValue("email", (object?)email ?? string.Empty);
		cmd.Parameters.AddWithValue("display_name", (object?)displayName ?? string.Empty);
		cmd.Parameters.AddWithValue("is_employee", isEmployee);

		UserRow row;
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				throw new InvalidOperationException(
					$"User upsert for oid {oid:D} returned no row. RLS may have suppressed the insert; "
					+ "confirm 0101-users-self-provisioning.sql is applied.");
			}

			row = MapUser(reader);
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		// Invalidate any cached projection since is_employee / email /
		// display_name may have changed. Authorizations didn't change in
		// this code path but they might have been refreshed by an admin
		// out-of-band, so the safe thing is to drop the cache entry.
		InvalidateCache(oid);

		_logger.LogDebug("EnsureUser oid={Oid} is_employee={IsEmp}", oid, isEmployee);
		return row;
	}

	/// <inheritdoc />
	public async Task<UserDirectoryProjection?> GetProjectionAsync(
		Guid oid,
		CancellationToken cancellationToken = default)
	{
		if (oid == Guid.Empty)
		{
			return null;
		}

		lock (_cacheLock)
		{
			if (_cache.TryGetValue(oid, out var cached))
			{
				return cached;
			}
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Minimal session for the self-read predicate to fire on
		// user_authorizations (user_id = app_user_id()). The users row
		// is_employee isn't known yet -- we don't need it for the read,
		// p_users_read just requires authenticated.
		var minimal = new RlsSessionContext(
			UserId: oid,
			IsAuthenticated: true,
			IsEmployee: false,
			HomeDepartmentIds: Array.Empty<Guid>(),
			ContributorDepartmentIds: Array.Empty<Guid>(),
			ReviewerDepartmentIds: Array.Empty<Guid>(),
			LibrarianDepartmentIds: Array.Empty<Guid>(),
			IsAdmin: false,
			PersonaId: null);
		await RlsSessionPusher.PushAsync(conn, minimal, cancellationToken).ConfigureAwait(false);

		const string userSql = """
			SELECT id, email, display_name, is_employee, deactivated_at, created_at
			FROM users
			WHERE id = @oid
			""";

		UserRow? user;
		await using (var userCmd = new NpgsqlCommand(userSql, conn, tx))
		{
			userCmd.Parameters.AddWithValue("oid", oid);
			await using var reader = await userCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			user = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
				? MapUser(reader)
				: null;
		}

		if (user is null)
		{
			await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
			return null;
		}

		const string authSql = """
			SELECT user_id, department_id, role, source_group_id, granted_at
			FROM user_authorizations
			WHERE user_id = @oid
			""";

		var auths = new List<UserAuthorization>();
		await using (var authCmd = new NpgsqlCommand(authSql, conn, tx))
		{
			authCmd.Parameters.AddWithValue("oid", oid);
			await using var reader = await authCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				if (!Enum.TryParse<Role>(reader.GetString(2), ignoreCase: false, out var role))
				{
					_logger.LogWarning(
						"Skipping user_authorizations row with unknown role={Role} user_id={UserId}",
						reader.GetString(2),
						reader.GetGuid(0));
					continue;
				}

				auths.Add(new UserAuthorization(
					UserId: reader.GetGuid(0),
					DepartmentId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
					Role: role,
					SourceGroupId: reader.GetString(3),
					GrantedAt: reader.GetFieldValue<DateTimeOffset>(4)));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		var projection = new UserDirectoryProjection(user, auths);
		lock (_cacheLock)
		{
			_cache[oid] = projection;
		}

		return projection;
	}

	private void InvalidateCache(Guid oid)
	{
		lock (_cacheLock)
		{
			_cache.Remove(oid);
		}
	}

	private static UserRow MapUser(NpgsqlDataReader reader) => new(
		Id: reader.GetGuid(0),
		Email: reader.IsDBNull(1) ? null : reader.GetString(1),
		DisplayName: reader.IsDBNull(2) ? null : reader.GetString(2),
		IsEmployee: reader.GetBoolean(3),
		DeactivatedAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
		CreatedAt: reader.GetFieldValue<DateTimeOffset>(5));
}
