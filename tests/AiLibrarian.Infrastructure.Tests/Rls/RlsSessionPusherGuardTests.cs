using AiLibrarian.Infrastructure.Rls;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// The plan-agent-flagged pitfall: <c>SET LOCAL</c> only takes effect
/// inside an active transaction. If a test (or production code) calls
/// <c>RlsSessionPusher.PushAsync</c> on a connection that hasn't begun
/// a transaction, every subsequent RLS predicate sees the default
/// (empty) session vars — producing green tests that prove nothing.
///
/// <para>
/// This test pins the contract documented in the pusher's XML header.
/// Lives in the no-Docker tier because it doesn't need a live database;
/// it simply asserts that values pushed without a transaction are not
/// observable on the same connection.
/// </para>
/// </summary>
public sealed class RlsSessionPusherGuardTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public RlsSessionPusherGuardTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task Push_without_transaction_is_silently_ineffective()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);

		// Note: NO transaction wrapper around the push.
		await RlsSessionPusher.PushAsync(
			conn,
			RlsTestData.ContextFor(
				RlsTestData.EngineeringContributorId,
				isEmployee: true,
				isAdmin: true,
				homeDepts: [RlsTestData.EngineeringDeptId])).ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand("SELECT current_setting('app.user_id', true)", conn);
		var observed = await cmd.ExecuteScalarAsync().ConfigureAwait(false) as string;

		// Without a transaction, SET LOCAL is a no-op — the session
		// variable comes back empty even though the pusher "succeeded."
		// Production callers must wrap their work in BeginTransactionAsync;
		// this test exists so any future refactor that breaks that
		// invariant gets a clear signal in CI.
		observed.Should().BeNullOrEmpty(
			because: "SET LOCAL outside a transaction is silently dropped — wrap pusher calls in BeginTransactionAsync.");
	}

	[SkippableFact]
	public async Task Push_inside_transaction_is_observable()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

		var ctx = RlsTestData.ContextFor(
			RlsTestData.EngineeringContributorId,
			isEmployee: true,
			isAdmin: false,
			homeDepts: [RlsTestData.EngineeringDeptId]);
		await RlsSessionPusher.PushAsync(conn, ctx).ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand("SELECT current_setting('app.user_id', true)", conn, tx);
		var observed = await cmd.ExecuteScalarAsync().ConfigureAwait(false) as string;

		observed.Should().Be(RlsTestData.EngineeringContributorId.ToString("D"));

		await tx.CommitAsync().ConfigureAwait(false);
	}
}
