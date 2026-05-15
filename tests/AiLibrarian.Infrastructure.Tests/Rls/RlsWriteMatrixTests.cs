using AiLibrarian.Infrastructure.Rls;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// Write-side RLS matrix — pins <c>p_sources_write</c>:
/// only contributors-on-the-target-department or admins may INSERT.
/// Crucially, having a contributor role in dept A does not grant
/// write access to dept B.
/// </summary>
public sealed class RlsWriteMatrixTests : IClassFixture<RlsPostgresFixture>, IAsyncLifetime
{
	private readonly RlsPostgresFixture _fixture;

	public RlsWriteMatrixTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		if (!_fixture.IsAvailable)
		{
			return;
		}

		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString).ConfigureAwait(false);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[SkippableFact]
	public async Task Contributor_can_insert_into_their_own_department()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var id = await RlsTestData.InsertSourceAsync(
			_fixture.ConnectionString,
			RlsTestData.EngineeringDeptId,
			"Internal",
			RlsTestData.EngineeringContributorId,
			[RlsTestData.EngineeringDeptId],
			"contributor-write-allowed").ConfigureAwait(false);

		id.Should().NotBe(Guid.Empty);
	}

	[SkippableFact]
	public async Task Contributor_cannot_insert_into_a_department_they_dont_contribute_to()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var act = async () => await RlsTestData.InsertSourceAsync(
			_fixture.ConnectionString,
			RlsTestData.FinanceDeptId,
			"Internal",
			RlsTestData.EngineeringContributorId,
			contributorDepts: [RlsTestData.EngineeringDeptId],
			"cross-dept-write-blocked").ConfigureAwait(false);

		(await act.Should().ThrowAsync<PostgresException>())
			.Which.SqlState.Should().Be("42501",
				because: "PostgreSQL: insufficient_privilege — RLS write predicate must reject cross-department writes.");
	}

	[SkippableFact]
	public async Task Admin_can_insert_into_any_department()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

		await RlsSessionPusher.PushAsync(
			conn,
			RlsTestData.ContextFor(RlsTestData.AdminUserId, isEmployee: true, isAdmin: true)).ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand("""
			INSERT INTO sources (department_id, classification, title, content_type, contributed_by, approved_by, approved_at)
			VALUES (@dept, 'Internal', 'admin-cross-dept-write', 'text/markdown', @uid, @uid, now())
			RETURNING id
			""", conn, tx);
		cmd.Parameters.AddWithValue("dept", RlsTestData.FinanceDeptId);
		cmd.Parameters.AddWithValue("uid", RlsTestData.AdminUserId);

		var id = (Guid)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
		await tx.CommitAsync().ConfigureAwait(false);

		id.Should().NotBe(Guid.Empty);
	}
}
