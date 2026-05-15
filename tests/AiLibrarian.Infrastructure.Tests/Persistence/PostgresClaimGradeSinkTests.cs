using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Pins the Postgres-backed claim-grade sink against a real pgvector
/// container. Verifies the upsert keyed by (claim_id, grader_version),
/// the per-claim latest-by-graded_at read, and the system-admin write
/// path through the new <c>p_wiki_claim_grades_write</c> policy.
/// </summary>
public sealed class PostgresClaimGradeSinkTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresClaimGradeSinkTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task RecordAsync_Inserts_Then_Upserts_By_ClaimId_And_GraderVersion()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (claimId, _, _) = await SeedClaimAsync();

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var sink = new PostgresClaimGradeSink(dataSource, NullLogger<PostgresClaimGradeSink>.Instance);

		// First record -- insert.
		await sink.RecordAsync(
			new ClaimGrade(claimId, ClaimVerdict.Supported, 0.95, "matches source"),
			graderVersion: "gpt-4o-test");

		var latest = await sink.GetLatestAsync(claimId);
		latest.Should().NotBeNull();
		latest!.Verdict.Should().Be(ClaimVerdict.Supported);
		latest.Confidence.Should().BeApproximately(0.95, 0.001);

		// Same (claim, version) -- upsert.
		await sink.RecordAsync(
			new ClaimGrade(claimId, ClaimVerdict.Partial, 0.6, "revised: partial match"),
			graderVersion: "gpt-4o-test");

		latest = await sink.GetLatestAsync(claimId);
		latest!.Verdict.Should().Be(ClaimVerdict.Partial);
		latest.Rationale.Should().Be("revised: partial match");
	}

	[SkippableFact]
	public async Task Different_GraderVersions_Coexist_And_Latest_Wins()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (claimId, _, _) = await SeedClaimAsync();
		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var sink = new PostgresClaimGradeSink(dataSource, NullLogger<PostgresClaimGradeSink>.Instance);

		await sink.RecordAsync(
			new ClaimGrade(claimId, ClaimVerdict.Supported, 0.8, "v1"),
			graderVersion: "gpt-4o-2026-01");

		// Brief delay so graded_at is strictly later. Postgres's now()
		// has microsecond resolution; one round-trip's worth of latency
		// is plenty.
		await Task.Delay(50);

		await sink.RecordAsync(
			new ClaimGrade(claimId, ClaimVerdict.NotSupported, 0.7, "v2"),
			graderVersion: "gpt-4o-2026-02");

		var latest = await sink.GetLatestAsync(claimId);
		latest!.Verdict.Should().Be(ClaimVerdict.NotSupported);

		var snapshot = await sink.SnapshotAsync();
		snapshot.Should().ContainSingle(g => g.ClaimId == claimId);
		// Snapshot is "latest per claim" by DISTINCT ON; v2 wins.
		snapshot.Single().Verdict.Should().Be(ClaimVerdict.NotSupported);
	}

	[SkippableFact]
	public async Task GetLatestAsync_Returns_Null_When_No_Grades()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var sink = new PostgresClaimGradeSink(dataSource, NullLogger<PostgresClaimGradeSink>.Instance);

		var result = await sink.GetLatestAsync(Guid.NewGuid());
		result.Should().BeNull();
	}

	[SkippableFact]
	public async Task PostgresChunkLookup_Reports_SoftDeleted_When_Source_Is_SoftDeleted()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (_, chunkId, sourceId) = await SeedClaimAsync();

		// Soft-delete the source as superuser (BYPASSRLS).
		await using (var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString))
		{
			await conn.OpenAsync();
			await using var cmd = new NpgsqlCommand(
				"UPDATE sources SET soft_deleted_at = now() WHERE id = @id",
				conn);
			cmd.Parameters.AddWithValue("id", sourceId);
			await cmd.ExecuteNonQueryAsync();
		}

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var lookup = new PostgresChunkLookup(dataSource, NullLogger<PostgresChunkLookup>.Instance);

		var resolved = await lookup.ResolveAsync(new[] { chunkId });
		resolved.Should().ContainKey(chunkId);
		resolved[chunkId].IsSoftDeleted.Should().BeTrue();
	}

	[SkippableFact]
	public async Task PostgresChunkLookup_Omits_Missing_Chunks_From_Result()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var lookup = new PostgresChunkLookup(dataSource, NullLogger<PostgresChunkLookup>.Instance);

		var unknown = Guid.NewGuid();
		var result = await lookup.ResolveAsync(new[] { unknown });
		result.Should().NotContainKey(unknown);
	}

	/// <summary>
	/// Seed: one approved source + one chunk in the Engineering dept + one
	/// minimal wiki page + facet + revision + claim, all via superuser so
	/// RLS doesn't get in the way of fixture prep. Returns the ids the
	/// tests need.
	/// </summary>
	private async Task<(Guid ClaimId, Guid ChunkId, Guid SourceId)> SeedClaimAsync()
	{
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var sourceId = await RlsTestData.InsertSourceAsync(
			_fixture.ConnectionString,
			departmentId: RlsTestData.EngineeringDeptId,
			classification: "Internal",
			contributorId: RlsTestData.EngineeringContributorId,
			contributorDepts: new[] { RlsTestData.EngineeringDeptId });

		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();

		// Insert a chunk
		Guid chunkId;
		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO source_chunks (source_id, order_index, content_markdown, span_anchor)
			VALUES (@source_id, 0, 'test chunk content', '{"type":"test"}'::jsonb)
			RETURNING id
			""", conn))
		{
			cmd.Parameters.AddWithValue("source_id", sourceId);
			chunkId = (Guid)(await cmd.ExecuteScalarAsync())!;
		}

		Guid pageId;
		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO wiki_pages (department_id, slug, title)
			VALUES (@dept, 'test-page', 'Test Page')
			RETURNING id
			""", conn))
		{
			cmd.Parameters.AddWithValue("dept", RlsTestData.EngineeringDeptId);
			pageId = (Guid)(await cmd.ExecuteScalarAsync())!;
		}

		await using (var facetCmd = new NpgsqlCommand("""
			INSERT INTO page_facets (page_id, min_classification, body_markdown)
			VALUES (@page_id, 'Internal', '')
			""", conn))
		{
			facetCmd.Parameters.AddWithValue("page_id", pageId);
			await facetCmd.ExecuteNonQueryAsync();
		}

		Guid revisionId;
		await using (var revCmd = new NpgsqlCommand("""
			INSERT INTO wiki_page_revisions (page_id, min_classification, revision_number, authored_by, body_markdown)
			VALUES (@page_id, 'Internal', 1, '00000000-0000-0000-0000-00000000ffff', '')
			RETURNING id
			""", conn))
		{
			revCmd.Parameters.AddWithValue("page_id", pageId);
			revisionId = (Guid)(await revCmd.ExecuteScalarAsync())!;
		}

		Guid claimId;
		await using (var claimCmd = new NpgsqlCommand("""
			INSERT INTO wiki_claims (revision_id, claim_text, position, facet_classification)
			VALUES (@rev_id, 'test claim text', 0, 'Internal')
			RETURNING id
			""", conn))
		{
			claimCmd.Parameters.AddWithValue("rev_id", revisionId);
			claimId = (Guid)(await claimCmd.ExecuteScalarAsync())!;
		}

		return (claimId, chunkId, sourceId);
	}
}
