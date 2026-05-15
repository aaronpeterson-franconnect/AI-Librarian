using AiLibrarian.Infrastructure.Rls;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// Pin the append-only contract from changeset
/// <c>0006-audit-events-3</c>: row-level triggers raise on
/// <c>UPDATE</c> and <c>DELETE</c> regardless of caller role. Belt
/// and suspenders behind the RLS write predicate — an admin who
/// could in principle have write privileges still can't mutate the
/// ledger.
/// </summary>
public sealed class AuditAppendOnlyTests : IClassFixture<RlsPostgresFixture>, IAsyncLifetime
{
	private readonly RlsPostgresFixture _fixture;
	private Guid _seededEventId;
	private DateTimeOffset _seededOccurredAt;

	public AuditAppendOnlyTests(RlsPostgresFixture fixture)
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

		// Seed one row to mutate against.
		await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System()).ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand("""
			INSERT INTO audit_events (actor_user_id, event_type, event_subtype, correlation_id, outcome)
			VALUES (@actor, 'test', 'append_only_seed', @corr, 'Success')
			RETURNING id, occurred_at
			""", conn, tx);
		cmd.Parameters.AddWithValue("actor", RlsTestData.AdminUserId);
		cmd.Parameters.AddWithValue("corr", Guid.NewGuid());

		await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
		await reader.ReadAsync().ConfigureAwait(false);
		_seededEventId = reader.GetGuid(0);
		_seededOccurredAt = reader.GetFieldValue<DateTimeOffset>(1);
		await reader.CloseAsync().ConfigureAwait(false);
		await tx.CommitAsync().ConfigureAwait(false);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[SkippableFact]
	public async Task Update_on_audit_events_is_blocked_by_trigger()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var act = () => MutateAsync(
			"UPDATE audit_events SET outcome = 'Failure' WHERE id = @id AND occurred_at = @when",
			_seededEventId, _seededOccurredAt);

		(await act.Should().ThrowAsync<PostgresException>())
			.Which.MessageText.Should().Contain("audit_events is append-only");
	}

	[SkippableFact]
	public async Task Delete_on_audit_events_is_blocked_by_trigger()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var act = () => MutateAsync(
			"DELETE FROM audit_events WHERE id = @id AND occurred_at = @when",
			_seededEventId, _seededOccurredAt);

		(await act.Should().ThrowAsync<PostgresException>())
			.Which.MessageText.Should().Contain("audit_events is append-only");
	}

	private async Task MutateAsync(string sql, Guid id, DateTimeOffset occurredAt)
	{
		// Connect as the testcontainer superuser explicitly so RLS doesn't
		// silently short-circuit the UPDATE/DELETE to a 0-row no-op. The
		// point of this test is to prove the BEFORE-trigger from
		// 0006-audit-events-3 actually fires — the "belt and suspenders"
		// defense behind the RLS write predicates. Production uses both
		// layers; the trigger has to fire on its own merits.
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync().ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("id", id);
		cmd.Parameters.AddWithValue("when", occurredAt);
		await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
		await tx.CommitAsync().ConfigureAwait(false);
	}
}
