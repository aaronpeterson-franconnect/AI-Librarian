using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Testcontainer coverage for the source-type backfill. Closes the
/// ADR 0015 sourceTypeWeights thread by proving the maintenance path:
/// rows created before the classifier wiring (or any future ingestion
/// path that forgets to stamp source_type) can be retroactively
/// classified in bounded batches.
/// </summary>
public sealed class PostgresSourceTypeBackfillerTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresSourceTypeBackfillerTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task BackfillBatchAsync_Classifies_Null_Rows_And_Reports_Remaining()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);
		await ClearTestSourcesAsync();

		// Insert three rows directly (bypass the writer's classifier
		// stamping) so source_type is NULL.
		await InsertSourceWithNullTypeAsync("Service Bus runbook", "text/markdown");
		await InsertSourceWithNullTypeAsync("JIRA-1234 investigation", "text/markdown");
		await InsertSourceWithNullTypeAsync("Generic spec", "application/pdf");

		var backfiller = CreateBackfiller();
		var outcome = await backfiller.BackfillBatchAsync(batchSize: 10);

		outcome.ClassifiedThisCall.Should().Be(3);
		outcome.RemainingUnclassified.Should().Be(0);
		outcome.ClassificationCounts.Should().ContainKey(SourceType.Runbook);
		outcome.ClassificationCounts.Should().ContainKey(SourceType.Ticket);
		outcome.ClassificationCounts.Should().ContainKey(SourceType.Document);
	}

	[SkippableFact]
	public async Task BackfillBatchAsync_Honours_Batch_Size()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);
		await ClearTestSourcesAsync();

		// Insert 5 rows; ask for batch=2 -> classifies 2 + reports 3
		// remaining. Second call closes the gap.
		for (var i = 0; i < 5; i++)
		{
			await InsertSourceWithNullTypeAsync($"Generic upload #{i}", "application/pdf");
		}

		var backfiller = CreateBackfiller();

		var first = await backfiller.BackfillBatchAsync(batchSize: 2);
		first.ClassifiedThisCall.Should().Be(2);
		first.RemainingUnclassified.Should().Be(3);

		var second = await backfiller.BackfillBatchAsync(batchSize: 10);
		second.ClassifiedThisCall.Should().Be(3);
		second.RemainingUnclassified.Should().Be(0);
	}

	[SkippableFact]
	public async Task BackfillBatchAsync_Is_Idempotent_On_Already_Classified_Rows()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);
		await ClearTestSourcesAsync();

		// Pre-classify a row (simulating a row that the live INSERT
		// path already stamped). Backfill should skip it.
		await InsertSourceWithExplicitTypeAsync("Already classified", "text/markdown", SourceType.Document);
		await InsertSourceWithNullTypeAsync("Needs classification", "text/markdown");

		var backfiller = CreateBackfiller();
		var outcome = await backfiller.BackfillBatchAsync(batchSize: 10);

		outcome.ClassifiedThisCall.Should().Be(1, "only the null-typed row should be touched");
		outcome.RemainingUnclassified.Should().Be(0);
	}

	[SkippableFact]
	public async Task BackfillBatchAsync_With_No_Pending_Rows_Returns_Zero()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);
		await ClearTestSourcesAsync();

		var backfiller = CreateBackfiller();
		var outcome = await backfiller.BackfillBatchAsync(batchSize: 100);

		outcome.ClassifiedThisCall.Should().Be(0);
		outcome.RemainingUnclassified.Should().Be(0);
		outcome.ClassificationCounts.Should().BeEmpty();
	}

	// --- helpers ---

	private PostgresSourceTypeBackfiller CreateBackfiller()
	{
		var ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
		return new PostgresSourceTypeBackfiller(ds, NullLogger<PostgresSourceTypeBackfiller>.Instance);
	}

	private async Task ClearTestSourcesAsync()
	{
		// Hard-delete every source row to give each test a clean slate
		// against the remaining-count assertion. RLS doesn't apply to
		// the superuser connection.
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("DELETE FROM sources", conn);
		await cmd.ExecuteNonQueryAsync();
	}

	private async Task<Guid> InsertSourceWithNullTypeAsync(string title, string contentType)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO sources (department_id, classification, title, content_type, contributed_by, approved_by, approved_at)
			VALUES (@dept, 'Internal', @title, @ct, @user, @user, now())
			RETURNING id
			""", conn);
		cmd.Parameters.AddWithValue("dept", RlsTestData.EngineeringDeptId);
		cmd.Parameters.AddWithValue("title", title);
		cmd.Parameters.AddWithValue("ct", contentType);
		cmd.Parameters.AddWithValue("user", RlsTestData.EngineeringContributorId);
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}

	private async Task<Guid> InsertSourceWithExplicitTypeAsync(string title, string contentType, string sourceType)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO sources (department_id, classification, title, content_type, contributed_by, approved_by, approved_at, source_type)
			VALUES (@dept, 'Internal', @title, @ct, @user, @user, now(), @st)
			RETURNING id
			""", conn);
		cmd.Parameters.AddWithValue("dept", RlsTestData.EngineeringDeptId);
		cmd.Parameters.AddWithValue("title", title);
		cmd.Parameters.AddWithValue("ct", contentType);
		cmd.Parameters.AddWithValue("user", RlsTestData.EngineeringContributorId);
		cmd.Parameters.AddWithValue("st", sourceType);
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}
}
