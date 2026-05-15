using AiLibrarian.Domain;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;
using NpgsqlTypes;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Coverage for the Phase 2.5 proposal-queue polish endpoints:
/// <see cref="IWikiProposalWriter.BulkRejectAsync"/> and
/// <see cref="IWikiProposalReader.ListDecidedAsync"/>.
/// </summary>
public sealed class PostgresWikiProposalQueueTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresWikiProposalQueueTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task BulkRejectAsync_Classifies_Rejected_Skipped_NotFound()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var page = await CreatePageAsync();
		// Three pending proposals on three distinct facets so the
		// "one pending per facet" unique index doesn't conflict.
		var pending1 = await InsertProposalAsync(page, classification: "Internal");
		var pending2 = await InsertProposalAsync(page, classification: "Confidential");
		var alreadyRejected = await InsertProposalAsync(page, classification: "Restricted");
		await TransitionAsync(alreadyRejected, state: "rejected", reason: "previously");
		var unknown = Guid.NewGuid();

		var writer = CreateWriter();
		var outcome = await writer.BulkRejectAsync(
			new[] { pending1, pending2, alreadyRejected, unknown },
			decidedBy: RlsTestData.EngineeringContributorId,
			reason: "Source X retired");

		outcome.Rejected.Should().BeEquivalentTo(new[] { pending1, pending2 });
		outcome.Skipped.Should().BeEquivalentTo(new[] { alreadyRejected });
		outcome.NotFound.Should().BeEquivalentTo(new[] { unknown });

		// The two pending rows are now rejected with the supplied reason.
		(await ScalarAsync<string>(
			"SELECT state FROM wiki_proposed_revisions WHERE id = @id",
			("id", pending1)))
			.Should().Be("rejected");
		(await ScalarAsync<string>(
			"SELECT decision_reason FROM wiki_proposed_revisions WHERE id = @id",
			("id", pending1)))
			.Should().Be("Source X retired");
	}

	[SkippableFact]
	public async Task BulkRejectAsync_With_Empty_List_Returns_Empty_Outcome()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var outcome = await writer.BulkRejectAsync(
			Array.Empty<Guid>(),
			decidedBy: RlsTestData.EngineeringContributorId,
			reason: "n/a");

		outcome.Rejected.Should().BeEmpty();
		outcome.Skipped.Should().BeEmpty();
		outcome.NotFound.Should().BeEmpty();
	}

	[SkippableFact]
	public async Task ListDecidedAsync_Filters_By_DecidedBy_And_Since()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var page = await CreatePageAsync();
		var librarian = RlsTestData.EngineeringContributorId;
		var otherDecider = await InsertOtherUserAsync();

		// Three decided proposals: two by librarian (one old, one recent),
		// one by another decider (recent). Plus one pending row that must
		// never appear in the result.
		var oldByMe = await InsertProposalAsync(page, classification: "Internal");
		await TransitionAsync(oldByMe, state: "rejected", decider: librarian, decidedAt: DateTimeOffset.UtcNow.AddDays(-30), reason: "old");

		var recentByMe = await InsertProposalAsync(page, classification: "Confidential");
		await TransitionAsync(recentByMe, state: "rejected", decider: librarian, decidedAt: DateTimeOffset.UtcNow.AddMinutes(-5), reason: "recent");

		var recentByOther = await InsertProposalAsync(page, classification: "Restricted");
		await TransitionAsync(recentByOther, state: "rejected", decider: otherDecider, decidedAt: DateTimeOffset.UtcNow.AddMinutes(-2), reason: "by other");

		var stillPending = await InsertProposalAsync(page, classification: "Public");

		var reader = CreateReader();

		// Filter by librarian: returns both of theirs, ordered newest first.
		var byMe = await reader.ListDecidedAsync(decidedBy: librarian, since: null, limit: 50);
		byMe.Select(p => p.Id).Should().BeEquivalentTo(new[] { recentByMe, oldByMe }, opts => opts.WithStrictOrdering());

		// Filter by librarian + since 1 day ago: only the recent one.
		var recentByMeOnly = await reader.ListDecidedAsync(
			decidedBy: librarian,
			since: DateTimeOffset.UtcNow.AddDays(-1),
			limit: 50);
		recentByMeOnly.Select(p => p.Id).Should().BeEquivalentTo(new[] { recentByMe });

		// No decidedBy filter: includes the other decider's row, excludes the pending one.
		var all = await reader.ListDecidedAsync(decidedBy: null, since: null, limit: 50);
		all.Select(p => p.Id).Should().Contain(new[] { recentByMe, recentByOther, oldByMe });
		all.Select(p => p.Id).Should().NotContain(stillPending);
	}

	// --- helpers ---

	private PostgresWikiProposalWriter CreateWriter()
	{
		var ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
		return new PostgresWikiProposalWriter(ds, NullLogger<PostgresWikiProposalWriter>.Instance);
	}

	private PostgresWikiProposalReader CreateReader()
	{
		var ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
		return new PostgresWikiProposalReader(ds, NullLogger<PostgresWikiProposalReader>.Instance);
	}

	private async Task<Guid> CreatePageAsync()
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		var slug = $"queue-{Guid.NewGuid():N}"[..30].ToLowerInvariant();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO wiki_pages (department_id, slug, title)
			VALUES (@d, @s, 'Queue Test Page')
			RETURNING id
			""", conn);
		cmd.Parameters.AddWithValue("d", RlsTestData.EngineeringDeptId);
		cmd.Parameters.AddWithValue("s", slug);
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertProposalAsync(Guid pageId, string classification)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO wiki_proposed_revisions
				(page_id, min_classification, proposed_revision_number, authored_by, body_markdown, proposed_payload, state)
			VALUES (@p, @c, 1, @author, 'Body.', @payload::jsonb, 'pending')
			RETURNING id
			""", conn);
		cmd.Parameters.AddWithValue("p", pageId);
		cmd.Parameters.AddWithValue("c", classification);
		cmd.Parameters.AddWithValue("author", RlsTestData.EngineeringContributorId);
		var payloadParam = cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Text, """{"claims":[]}""");
		_ = payloadParam;
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}

	private async Task TransitionAsync(
		Guid proposalId,
		string state,
		string reason,
		Guid? decider = null,
		DateTimeOffset? decidedAt = null)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			UPDATE wiki_proposed_revisions
			SET state = @s, decided_by = @decider, decided_at = @when, decision_reason = @reason
			WHERE id = @id
			""", conn);
		cmd.Parameters.AddWithValue("s", state);
		cmd.Parameters.AddWithValue("decider", decider ?? RlsTestData.EngineeringContributorId);
		cmd.Parameters.AddWithValue("when", decidedAt ?? DateTimeOffset.UtcNow);
		cmd.Parameters.AddWithValue("reason", reason);
		cmd.Parameters.AddWithValue("id", proposalId);
		await cmd.ExecuteNonQueryAsync();
	}

	private async Task<Guid> InsertOtherUserAsync()
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO users (id, email, display_name, is_employee)
			VALUES (gen_random_uuid(), @email, 'Other librarian', true)
			RETURNING id
			""", conn);
		cmd.Parameters.AddWithValue("email", $"other-{Guid.NewGuid():N}@test.local");
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}

	private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand(sql, conn);
		foreach (var (n, v) in parameters)
		{
			cmd.Parameters.AddWithValue(n, v);
		}
		return (T)(await cmd.ExecuteScalarAsync())!;
	}
}
