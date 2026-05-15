using AiLibrarian.Infrastructure.Rls;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// Identity + source fixture builder for the RLS battery. Inserts
/// minimal department / user / source rows that exercise every branch
/// of the read predicate from <c>0099-rls-policies.sql</c>.
/// </summary>
internal static class RlsTestData
{
	public static readonly Guid EngineeringDeptId = new("00000001-0000-0000-0000-000000000001");
	public static readonly Guid FinanceDeptId = new("00000001-0000-0000-0000-000000000002");

	public static readonly Guid EngineeringContributorId = new("00000002-0000-0000-0000-000000000001");
	public static readonly Guid EngineeringLibrarianId = new("00000002-0000-0000-0000-000000000002");
	public static readonly Guid FinanceReaderId = new("00000002-0000-0000-0000-000000000003");
	public static readonly Guid AdminUserId = new("00000002-0000-0000-0000-000000000004");
	public static readonly Guid GuestUserId = new("00000002-0000-0000-0000-000000000005");

	/// <summary>
	/// Build a session context that reflects the user's home depts +
	/// roles. Mirrors what the API's <c>SessionContextBuilder</c>
	/// would produce from Entra claims; fixture-only because the test
	/// harness doesn't run an Entra-issued JWT through.
	/// </summary>
	public static RlsSessionContext ContextFor(
		Guid userId,
		bool isEmployee,
		bool isAdmin,
		IReadOnlyCollection<Guid>? homeDepts = null,
		IReadOnlyCollection<Guid>? contributorDepts = null,
		IReadOnlyCollection<Guid>? reviewerDepts = null,
		IReadOnlyCollection<Guid>? librarianDepts = null)
		=> new(
			UserId: userId,
			IsAuthenticated: true,
			IsEmployee: isEmployee,
			HomeDepartmentIds: homeDepts ?? Array.Empty<Guid>(),
			ContributorDepartmentIds: contributorDepts ?? Array.Empty<Guid>(),
			ReviewerDepartmentIds: reviewerDepts ?? Array.Empty<Guid>(),
			LibrarianDepartmentIds: librarianDepts ?? Array.Empty<Guid>(),
			IsAdmin: isAdmin,
			PersonaId: null);

	/// <summary>
	/// Seed two departments, five users covering the canonical
	/// identity matrix, and the user_authorizations rows the RLS
	/// helpers expect. Idempotent on a fresh container.
	/// </summary>
	public static async Task SeedIdentitiesAsync(string connectionString, CancellationToken cancellationToken = default)
	{
		await using var conn = new NpgsqlConnection(connectionString);
		await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using (var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
		{
			// RLS policies on departments / users / user_authorizations
			// require admin for writes — push an admin context.
			await RlsSessionPusher.PushAsync(
				conn,
				ContextFor(AdminUserId, isEmployee: true, isAdmin: true),
				cancellationToken).ConfigureAwait(false);

			await ExecAsync(conn, tx, """
				INSERT INTO departments (id, name, display_name) VALUES
					(@eng_id, 'engineering', 'Engineering'),
					(@fin_id, 'finance', 'Finance')
				ON CONFLICT (id) DO NOTHING
				""", new Dictionary<string, object>
				{
					["eng_id"] = EngineeringDeptId,
					["fin_id"] = FinanceDeptId,
				}, cancellationToken).ConfigureAwait(false);

			await ExecAsync(conn, tx, """
				INSERT INTO users (id, display_name, is_employee) VALUES
					(@eng_contrib, 'Eng Contributor', true),
					(@eng_librarian, 'Eng Librarian', true),
					(@fin_reader, 'Fin Reader', true),
					(@admin, 'System Admin', true),
					(@guest, 'B2B Guest', false)
				ON CONFLICT (id) DO NOTHING
				""", new Dictionary<string, object>
				{
					["eng_contrib"] = EngineeringContributorId,
					["eng_librarian"] = EngineeringLibrarianId,
					["fin_reader"] = FinanceReaderId,
					["admin"] = AdminUserId,
					["guest"] = GuestUserId,
				}, cancellationToken).ConfigureAwait(false);

			await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Insert a source row with the given classification under a
	/// contributor's RLS context. Returns the new id.
	/// </summary>
	public static async Task<Guid> InsertSourceAsync(
		string connectionString,
		Guid departmentId,
		string classification,
		Guid contributorId,
		IReadOnlyCollection<Guid> contributorDepts,
		string title = "test source",
		CancellationToken cancellationToken = default)
	{
		await using var conn = new NpgsqlConnection(connectionString);
		await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await RlsSessionPusher.PushAsync(
			conn,
			ContextFor(contributorId, isEmployee: true, isAdmin: false, contributorDepts: contributorDepts),
			cancellationToken).ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand("""
			INSERT INTO sources (department_id, classification, title, content_type, contributed_by, approved_by, approved_at)
			VALUES (@dept, @cls, @title, 'text/markdown', @uid, @uid, now())
			RETURNING id
			""", conn, tx);
		cmd.Parameters.AddWithValue("dept", departmentId);
		cmd.Parameters.AddWithValue("cls", classification);
		cmd.Parameters.AddWithValue("title", title);
		cmd.Parameters.AddWithValue("uid", contributorId);

		var id = (Guid)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return id;
	}

	private static async Task ExecAsync(
		NpgsqlConnection conn,
		NpgsqlTransaction tx,
		string sql,
		Dictionary<string, object>? parameters,
		CancellationToken cancellationToken)
	{
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		if (parameters is not null)
		{
			foreach (var (k, v) in parameters)
			{
				cmd.Parameters.AddWithValue(k, v);
			}
		}

		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}
}
