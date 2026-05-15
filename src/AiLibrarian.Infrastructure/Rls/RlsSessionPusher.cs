using System.Globalization;

using Npgsql;

namespace AiLibrarian.Infrastructure.Rls;

/// <summary>
/// Pushes the session-variable set defined by <see cref="RlsSessionContext"/>
/// onto an open Npgsql connection using <c>SET LOCAL</c>. Per ADR 0005
/// these variables are the authoritative inputs to every RLS read and
/// write predicate (excluding <c>app.persona_id</c>, which is set here
/// for retrieval and persona-action authority but is intentionally
/// not consulted by any RLS predicate).
///
/// <para>
/// Must be called inside an active transaction; <c>SET LOCAL</c>
/// without a transaction is a no-op in Postgres.
/// </para>
/// </summary>
public static class RlsSessionPusher
{
	/// <summary>
	/// Push <paramref name="context"/> onto the given connection.
	/// </summary>
	/// <param name="connection">Open Npgsql connection.</param>
	/// <param name="context">Session context to push.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public static async Task PushAsync(
		NpgsqlConnection connection,
		RlsSessionContext context,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(connection);
		ArgumentNullException.ThrowIfNull(context);

		// Postgres SET LOCAL doesn't accept bind parameters (SQL state
		// 42601 — "syntax error at or near $1"). Use set_config() with
		// is_local=true instead; functionally equivalent and parameter-safe.
		const string sql = """
			SELECT
				set_config('app.user_id',           @user_id,           true),
				set_config('app.is_authenticated',  @is_authenticated,  true),
				set_config('app.is_employee',       @is_employee,       true),
				set_config('app.department_ids',    @department_ids,    true),
				set_config('app.contributor_depts', @contributor_depts, true),
				set_config('app.reviewer_depts',    @reviewer_depts,    true),
				set_config('app.librarian_depts',   @librarian_depts,   true),
				set_config('app.is_admin',          @is_admin,          true),
				set_config('app.persona_id',        @persona_id,        true)
			""";

		await using var cmd = new NpgsqlCommand(sql, connection);
		cmd.Parameters.AddWithValue("user_id", context.UserId.ToString());
		cmd.Parameters.AddWithValue("is_authenticated", BoolText(context.IsAuthenticated));
		cmd.Parameters.AddWithValue("is_employee", BoolText(context.IsEmployee));
		cmd.Parameters.AddWithValue("department_ids", JoinGuids(context.HomeDepartmentIds));
		cmd.Parameters.AddWithValue("contributor_depts", JoinGuids(context.ContributorDepartmentIds));
		cmd.Parameters.AddWithValue("reviewer_depts", JoinGuids(context.ReviewerDepartmentIds));
		cmd.Parameters.AddWithValue("librarian_depts", JoinGuids(context.LibrarianDepartmentIds));
		cmd.Parameters.AddWithValue("is_admin", BoolText(context.IsAdmin));
		cmd.Parameters.AddWithValue("persona_id", context.PersonaId?.ToString() ?? string.Empty);

		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static string BoolText(bool value)
		=> value ? "true" : "false";

	private static string JoinGuids(IReadOnlyCollection<Guid> values)
		=> values.Count == 0
			? string.Empty
			: string.Join(",", values.Select(v => v.ToString(format: "D", CultureInfo.InvariantCulture)));
}
