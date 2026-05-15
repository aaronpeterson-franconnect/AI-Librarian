using AiLibrarian.Infrastructure.Rls;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// Read-side RLS matrix — exercises every branch of the
/// <c>p_sources_read</c> policy from <c>0099-rls-policies.sql</c>:
/// Public is world-readable, Internal needs the employee flag,
/// Confidential needs department membership (or an admin / source
/// share), Restricted needs Librarian-or-higher.
///
/// <para>
/// Skipped silently when Docker isn't reachable. The harness reports
/// the reason in the test output so CI failures distinguish "Docker
/// missing" from "RLS broken."
/// </para>
/// </summary>
public sealed class RlsReadMatrixTests : IClassFixture<RlsPostgresFixture>, IAsyncLifetime
{
	private readonly RlsPostgresFixture _fixture;
	private Guid _publicSourceId;
	private Guid _internalSourceId;
	private Guid _confidentialEngineeringSourceId;
	private Guid _restrictedEngineeringSourceId;

	public RlsReadMatrixTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		if (!_fixture.IsAvailable)
		{
			return;
		}

		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		_publicSourceId = await RlsTestData.InsertSourceAsync(
			_fixture.ConnectionString,
			RlsTestData.EngineeringDeptId,
			"Public",
			RlsTestData.EngineeringContributorId,
			[RlsTestData.EngineeringDeptId],
			"public-runbook").ConfigureAwait(false);

		_internalSourceId = await RlsTestData.InsertSourceAsync(
			_fixture.ConnectionString,
			RlsTestData.EngineeringDeptId,
			"Internal",
			RlsTestData.EngineeringContributorId,
			[RlsTestData.EngineeringDeptId],
			"internal-runbook").ConfigureAwait(false);

		_confidentialEngineeringSourceId = await RlsTestData.InsertSourceAsync(
			_fixture.ConnectionString,
			RlsTestData.EngineeringDeptId,
			"Confidential",
			RlsTestData.EngineeringContributorId,
			[RlsTestData.EngineeringDeptId],
			"confidential-runbook").ConfigureAwait(false);

		_restrictedEngineeringSourceId = await RlsTestData.InsertSourceAsync(
			_fixture.ConnectionString,
			RlsTestData.EngineeringDeptId,
			"Restricted",
			RlsTestData.EngineeringContributorId,
			[RlsTestData.EngineeringDeptId],
			"restricted-runbook").ConfigureAwait(false);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[SkippableFact]
	public async Task Public_source_is_visible_to_finance_reader()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var visible = await ReadSourceAsync(
			_publicSourceId,
			RlsTestData.ContextFor(RlsTestData.FinanceReaderId, isEmployee: true, isAdmin: false));

		visible.Should().BeTrue("Public sources must be readable to any authenticated principal regardless of department.");
	}

	[SkippableFact]
	public async Task Internal_source_is_visible_to_other_department_employee()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var visible = await ReadSourceAsync(
			_internalSourceId,
			RlsTestData.ContextFor(
				RlsTestData.FinanceReaderId,
				isEmployee: true,
				isAdmin: false,
				homeDepts: [RlsTestData.FinanceDeptId]));

		visible.Should().BeTrue("Internal sources must be cross-department-readable to authenticated employees per ADR 0011.");
	}

	[SkippableFact]
	public async Task Internal_source_is_hidden_from_b2b_guest()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var visible = await ReadSourceAsync(
			_internalSourceId,
			RlsTestData.ContextFor(RlsTestData.GuestUserId, isEmployee: false, isAdmin: false));

		visible.Should().BeFalse("Internal-content reads require app.is_employee=true; B2B guests fall back to strict department membership.");
	}

	[SkippableFact]
	public async Task Confidential_source_is_hidden_from_other_department()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var visible = await ReadSourceAsync(
			_confidentialEngineeringSourceId,
			RlsTestData.ContextFor(
				RlsTestData.FinanceReaderId,
				isEmployee: true,
				isAdmin: false,
				homeDepts: [RlsTestData.FinanceDeptId]));

		visible.Should().BeFalse("Confidential reads need owning-department membership or a source share.");
	}

	[SkippableFact]
	public async Task Confidential_source_is_visible_to_owning_department_member()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var visible = await ReadSourceAsync(
			_confidentialEngineeringSourceId,
			RlsTestData.ContextFor(
				RlsTestData.EngineeringContributorId,
				isEmployee: true,
				isAdmin: false,
				homeDepts: [RlsTestData.EngineeringDeptId]));

		visible.Should().BeTrue("Confidential reads must be allowed for owning-department members.");
	}

	[SkippableFact]
	public async Task Restricted_source_is_hidden_from_owning_department_contributor()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var visible = await ReadSourceAsync(
			_restrictedEngineeringSourceId,
			RlsTestData.ContextFor(
				RlsTestData.EngineeringContributorId,
				isEmployee: true,
				isAdmin: false,
				homeDepts: [RlsTestData.EngineeringDeptId]));

		visible.Should().BeFalse("Restricted reads require Librarian+ in the owning department, not just membership.");
	}

	[SkippableFact]
	public async Task Restricted_source_is_visible_to_owning_department_librarian()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var visible = await ReadSourceAsync(
			_restrictedEngineeringSourceId,
			RlsTestData.ContextFor(
				RlsTestData.EngineeringLibrarianId,
				isEmployee: true,
				isAdmin: false,
				homeDepts: [RlsTestData.EngineeringDeptId],
				librarianDepts: [RlsTestData.EngineeringDeptId]));

		visible.Should().BeTrue("Restricted sources must be readable by the owning department's Librarian.");
	}

	[SkippableFact]
	public async Task Admin_sees_every_classification()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var ctx = RlsTestData.ContextFor(RlsTestData.AdminUserId, isEmployee: true, isAdmin: true);

		(await ReadSourceAsync(_publicSourceId, ctx)).Should().BeTrue();
		(await ReadSourceAsync(_internalSourceId, ctx)).Should().BeTrue();
		(await ReadSourceAsync(_confidentialEngineeringSourceId, ctx)).Should().BeTrue();
		(await ReadSourceAsync(_restrictedEngineeringSourceId, ctx)).Should().BeTrue();
	}

	private async Task<bool> ReadSourceAsync(Guid sourceId, RlsSessionContext context)
	{
		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, context).ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand("SELECT 1 FROM sources WHERE id = @id", conn, tx);
		cmd.Parameters.AddWithValue("id", sourceId);
		var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
		await tx.CommitAsync().ConfigureAwait(false);
		return result is not null;
	}
}
