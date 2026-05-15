using AiLibrarian.Domain;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Pins the cascade-regeneration reader's join + group-by shape
/// against a real Postgres. A wiki page with a chunk citation where
/// the underlying source has been soft-deleted should surface as one
/// dangling facet row; an intact citation should not.
/// </summary>
public sealed class DanglingFacetReaderTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public DanglingFacetReaderTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task SoftDeleted_Source_Surfaces_As_Dangling_Facet()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (pageId, chunkId, sourceId) = await SeedDanglingScenario();

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var reader = new DanglingFacetReader(dataSource, NullLogger<DanglingFacetReader>.Instance);

		var dangling = await reader.FindAsync(
			since: null,
			departmentId: RlsTestData.EngineeringDeptId,
			maxFacets: 50,
			cancellationToken: CancellationToken.None);

		dangling.Should().Contain(f =>
			f.PageId == pageId
			&& f.Classification == Classification.Internal
			&& f.DanglingCount > 0);
	}

	[SkippableFact]
	public async Task Healthy_Citation_Does_Not_Surface()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		// Seed but DON'T soft-delete the source.
		var (pageId, _) = await SeedHealthyPage();

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var reader = new DanglingFacetReader(dataSource, NullLogger<DanglingFacetReader>.Instance);

		var dangling = await reader.FindAsync(
			since: null,
			departmentId: RlsTestData.EngineeringDeptId,
			maxFacets: 50,
			cancellationToken: CancellationToken.None);

		dangling.Should().NotContain(f => f.PageId == pageId);
	}

	[SkippableFact]
	public async Task MaxFacets_Cap_Is_Honored()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		// Seed three dangling facets.
		await SeedDanglingScenario();
		await SeedDanglingScenario();
		await SeedDanglingScenario();

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var reader = new DanglingFacetReader(dataSource, NullLogger<DanglingFacetReader>.Instance);

		var dangling = await reader.FindAsync(
			since: null,
			departmentId: null,
			maxFacets: 2,
			cancellationToken: CancellationToken.None);

		dangling.Should().HaveCountLessThanOrEqualTo(2);
	}

	private async Task<(Guid PageId, Guid ChunkId, Guid SourceId)> SeedDanglingScenario()
	{
		var (pageId, chunkId, sourceId) = await SeedPageWithRevisionAndCitationAsync();

		// Soft-delete the source so the citation goes dangling.
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand(
			"UPDATE sources SET soft_deleted_at = now() WHERE id = @id",
			conn);
		cmd.Parameters.AddWithValue("id", sourceId);
		await cmd.ExecuteNonQueryAsync();

		return (pageId, chunkId, sourceId);
	}

	private async Task<(Guid PageId, Guid SourceId)> SeedHealthyPage()
	{
		var (pageId, _, sourceId) = await SeedPageWithRevisionAndCitationAsync();
		return (pageId, sourceId);
	}

	private async Task<(Guid PageId, Guid ChunkId, Guid SourceId)> SeedPageWithRevisionAndCitationAsync()
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

		Guid chunkId;
		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO source_chunks (source_id, order_index, content_markdown, span_anchor)
			VALUES (@s, 0, 'chunk body', '{"type":"test"}'::jsonb)
			RETURNING id
			""", conn))
		{
			cmd.Parameters.AddWithValue("s", sourceId);
			chunkId = (Guid)(await cmd.ExecuteScalarAsync())!;
		}

		var slug = $"dangling-page-{Guid.NewGuid():N}"[..40].ToLowerInvariant();
		Guid pageId;
		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO wiki_pages (department_id, slug, title)
			VALUES (@dept, @slug, 'Dangling Test')
			RETURNING id
			""", conn))
		{
			cmd.Parameters.AddWithValue("dept", RlsTestData.EngineeringDeptId);
			cmd.Parameters.AddWithValue("slug", slug);
			pageId = (Guid)(await cmd.ExecuteScalarAsync())!;
		}

		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO page_facets (page_id, min_classification, body_markdown)
			VALUES (@p, 'Internal', '')
			""", conn))
		{
			cmd.Parameters.AddWithValue("p", pageId);
			await cmd.ExecuteNonQueryAsync();
		}

		Guid revisionId;
		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO wiki_page_revisions (page_id, min_classification, revision_number, authored_by, body_markdown)
			VALUES (@p, 'Internal', 1, '00000000-0000-0000-0000-00000000ffff', 'body')
			RETURNING id
			""", conn))
		{
			cmd.Parameters.AddWithValue("p", pageId);
			revisionId = (Guid)(await cmd.ExecuteScalarAsync())!;
		}

		Guid claimId;
		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO wiki_claims (revision_id, claim_text, position, facet_classification)
			VALUES (@r, 'a claim', 0, 'Internal')
			RETURNING id
			""", conn))
		{
			cmd.Parameters.AddWithValue("r", revisionId);
			claimId = (Guid)(await cmd.ExecuteScalarAsync())!;
		}

		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO wiki_claim_citations (claim_id, chunk_id, span_start, span_end, confidence)
			VALUES (@c, @ch, 0, 100, 0.9)
			""", conn))
		{
			cmd.Parameters.AddWithValue("c", claimId);
			cmd.Parameters.AddWithValue("ch", chunkId);
			await cmd.ExecuteNonQueryAsync();
		}

		return (pageId, chunkId, sourceId);
	}
}
