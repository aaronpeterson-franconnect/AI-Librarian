using AiLibrarian.Infrastructure.Rls;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// Diagnostic facts that surface the actual session/role state. When
/// the read-matrix tests fail with "should be hidden but visible," the
/// most common causes are: (1) the test connection role still has
/// BYPASSRLS, (2) <c>set_config</c> didn't actually push the value,
/// or (3) the policy text drifted. These tests pin all three before
/// the matrix runs.
/// </summary>
public sealed class RlsDiagnosticTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public RlsDiagnosticTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task Test_role_does_not_bypass_rls()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand(
			"SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user",
			conn);
		await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
		await reader.ReadAsync().ConfigureAwait(false);

		var rolsuper = reader.GetBoolean(0);
		var rolbypassrls = reader.GetBoolean(1);

		rolsuper.Should().BeFalse(
			"the test role must not be SUPERUSER; superusers bypass every RLS policy and would silently mask access-control bugs.");
		rolbypassrls.Should().BeFalse(
			"the test role must not have BYPASSRLS; otherwise every read in the battery comes back as allowed.");
	}

	[SkippableFact]
	public async Task Bare_sources_select_does_not_recurse()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(
			conn,
			RlsTestData.ContextFor(
				RlsTestData.EngineeringContributorId,
				isEmployee: true,
				isAdmin: false,
				homeDepts: [RlsTestData.EngineeringDeptId])).ConfigureAwait(false);

		// Empty sources table — even so, RLS predicate evaluation runs.
		// If there's a recursion-detection bug, this minimal SELECT
		// surfaces it without any test data setup noise.
		await using var cmd = new NpgsqlCommand("SELECT 1 FROM sources LIMIT 1", conn, tx);
		var act = async () => await cmd.ExecuteScalarAsync().ConfigureAwait(false);

		await act.Should().NotThrowAsync(
			because: "p_sources_read must not recurse — the 0100 fix should have broken the cycle with p_source_shares_read.");

		await tx.CommitAsync().ConfigureAwait(false);
	}

	[SkippableFact]
	public async Task Source_shares_policy_is_non_recursive()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		// 0100-fix-source-shares-rls-recursion.sql replaced the old
		// p_source_shares_read with a non-recursive version. If migration
		// replay missed the file, the policy text would still mention
		// FROM sources and the read matrix would 42P17.
		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand(
			"SELECT qual FROM pg_policies WHERE tablename = 'source_shares' AND policyname = 'p_source_shares_read'",
			conn);
		var qual = (string?)await cmd.ExecuteScalarAsync().ConfigureAwait(false);

		qual.Should().NotBeNull("p_source_shares_read must exist after migrations replay.");
		qual!.Should().NotContain("FROM sources",
			because: "the recursion-breaking 0100 migration must drop the FROM-sources branch from p_source_shares_read.");
	}

	[SkippableFact]
	public async Task Pushed_session_vars_are_observable_inside_transaction()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

		var ctx = RlsTestData.ContextFor(
			RlsTestData.EngineeringContributorId,
			isEmployee: false,
			isAdmin: false,
			homeDepts: [RlsTestData.EngineeringDeptId],
			librarianDepts: [RlsTestData.FinanceDeptId]);
		await RlsSessionPusher.PushAsync(conn, ctx).ConfigureAwait(false);

		string userId, isEmployee, isAdmin, deptIds, librarianDepts;
		await using (var cmd = new NpgsqlCommand("""
			SELECT
				current_setting('app.user_id', true),
				current_setting('app.is_employee', true),
				current_setting('app.is_admin', true),
				current_setting('app.department_ids', true),
				current_setting('app.librarian_depts', true)
			""", conn, tx))
		{
			await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
			await reader.ReadAsync().ConfigureAwait(false);
			userId = reader.GetString(0);
			isEmployee = reader.GetString(1);
			isAdmin = reader.GetString(2);
			deptIds = reader.GetString(3);
			librarianDepts = reader.GetString(4);
		}

		userId.Should().Be(RlsTestData.EngineeringContributorId.ToString("D"));
		isEmployee.Should().Be("false", because: "set_config must round-trip the IsEmployee=false flag exactly.");
		isAdmin.Should().Be("false");
		deptIds.Should().Be(RlsTestData.EngineeringDeptId.ToString("D"));
		librarianDepts.Should().Be(RlsTestData.FinanceDeptId.ToString("D"));

		await tx.CommitAsync().ConfigureAwait(false);
	}
}
