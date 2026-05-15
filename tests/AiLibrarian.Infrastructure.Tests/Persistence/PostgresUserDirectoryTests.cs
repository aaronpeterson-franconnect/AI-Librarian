using AiLibrarian.Domain;
using AiLibrarian.Domain.Users;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Exercises the JIT provisioning path against a real pgvector
/// container so the new <c>p_users_self_insert</c> /
/// <c>p_users_self_update</c> policies from
/// <c>0101-users-self-provisioning.sql</c> are proven to actually
/// admit the insert under a non-admin RLS context.
/// </summary>
public sealed class PostgresUserDirectoryTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresUserDirectoryTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task EnsureUser_Inserts_Row_Then_Updates_OnConflict()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason ?? "Postgres fixture unavailable");

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var directory = new PostgresUserDirectory(dataSource, NullLogger<PostgresUserDirectory>.Instance);
		var oid = Guid.NewGuid();

		// First call: insert.
		var inserted = await directory.EnsureUserAsync(oid, "user@example.com", "Test User", isEmployee: true);
		inserted.Id.Should().Be(oid);
		inserted.Email.Should().Be("user@example.com");
		inserted.DisplayName.Should().Be("Test User");
		inserted.IsEmployee.Should().BeTrue();

		// Second call: update -- transition to guest, change name.
		var updated = await directory.EnsureUserAsync(oid, "guest@external.com", "Guest User", isEmployee: false);
		updated.Id.Should().Be(oid);
		updated.IsEmployee.Should().BeFalse();
		updated.DisplayName.Should().Be("Guest User");
		updated.Email.Should().Be("guest@external.com");
	}

	[SkippableFact]
	public async Task GetProjection_Returns_Null_For_Unknown_Oid()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var directory = new PostgresUserDirectory(dataSource, NullLogger<PostgresUserDirectory>.Instance);

		var result = await directory.GetProjectionAsync(Guid.NewGuid());
		result.Should().BeNull();
	}

	[SkippableFact]
	public async Task GetProjection_Reads_Self_Authorizations_Without_Admin_Context()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		// Seed two distinct depts so we can verify the role roll-up.
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var directory = new PostgresUserDirectory(dataSource, NullLogger<PostgresUserDirectory>.Instance);

		// JIT-provision a brand new user, then seed an authorization
		// row for them via admin context, then verify they can read it
		// back through self-read policy.
		var oid = Guid.NewGuid();
		await directory.EnsureUserAsync(oid, "x@y.z", "JIT User", isEmployee: true);

		await SeedAuthorizationAsAdmin(oid, RlsTestData.EngineeringDeptId, Role.Librarian);

		var projection = await directory.GetProjectionAsync(oid);

		projection.Should().NotBeNull();
		projection!.User.Id.Should().Be(oid);
		projection.Authorizations.Should().ContainSingle();
		projection.LibrarianDepartmentIds.Should().BeEquivalentTo(new[] { RlsTestData.EngineeringDeptId });
		projection.ReviewerDepartmentIds.Should().BeEquivalentTo(new[] { RlsTestData.EngineeringDeptId });
	}

	[SkippableFact]
	public async Task EnsureUser_Cannot_Insert_Someone_Elses_Row()
	{
		// Direct exercise of the self-insert WITH CHECK predicate. A
		// caller pushing oid A onto the RLS session cannot insert a
		// users row with id = oid B.
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		await using var conn = await dataSource.OpenConnectionAsync();
		await using var tx = await conn.BeginTransactionAsync();

		var callerOid = Guid.NewGuid();
		var targetOid = Guid.NewGuid();

		// Caller pushes their own OID...
		await RlsSessionPusher.PushAsync(
			conn,
			new RlsSessionContext(
				UserId: callerOid,
				IsAuthenticated: true,
				IsEmployee: true,
				HomeDepartmentIds: Array.Empty<Guid>(),
				ContributorDepartmentIds: Array.Empty<Guid>(),
				ReviewerDepartmentIds: Array.Empty<Guid>(),
				LibrarianDepartmentIds: Array.Empty<Guid>(),
				IsAdmin: false,
				PersonaId: null));

		// ...then tries to insert someone else's row.
		await using var cmd = new NpgsqlCommand(
			"INSERT INTO users (id, display_name, is_employee) VALUES (@id, 'evil', true)",
			conn,
			tx);
		cmd.Parameters.AddWithValue("id", targetOid);

		var act = async () => await cmd.ExecuteNonQueryAsync();
		// Postgres surfaces the policy violation as 42501 / "new row
		// violates row-level security policy". The Npgsql exception
		// message embeds that.
		await act.Should().ThrowAsync<PostgresException>()
			.Where(ex => ex.SqlState == "42501");
	}

	private async Task SeedAuthorizationAsAdmin(Guid userId, Guid departmentId, Role role)
	{
		// Use the superuser connection (BYPASSRLS implicit) to insert
		// the authorization row; the fixture exposes it for exactly this
		// kind of fixture-prep work.
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();

		await using var cmd = new NpgsqlCommand("""
			INSERT INTO user_authorizations (user_id, department_id, role, source_group_id)
			VALUES (@user_id, @dept_id, @role, @group)
			""", conn);
		cmd.Parameters.AddWithValue("user_id", userId);
		cmd.Parameters.AddWithValue("dept_id", departmentId);
		cmd.Parameters.AddWithValue("role", role.ToString());
		cmd.Parameters.AddWithValue("group", $"g-{role}-{departmentId:N}");
		await cmd.ExecuteNonQueryAsync();
	}
}
