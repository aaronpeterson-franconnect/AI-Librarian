using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Exercises the Postgres-backed wiki revision writer against a real
/// pgvector container. Verifies the transaction shape (revision +
/// claims + citations + facet update + page bump all commit
/// together), the COALESCE-on-PK facet match, and the failure modes
/// the schema's constraints enforce.
/// </summary>
public sealed class PostgresWikiRevisionWriterTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresWikiRevisionWriterTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task CommitAsync_Inserts_Revision_With_Claims_And_Citations()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (pageId, chunkId) = await SeedPageAsync();
		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var writer = new PostgresWikiRevisionWriter(dataSource, NullLogger<PostgresWikiRevisionWriter>.Instance);

		var draft = new WikiRevisionDraft(
			PageId: pageId,
			MinClassification: Classification.Internal,
			PersonaId: null,
			RevisionNumber: 1,
			AuthoredBy: new Guid("00000000-0000-0000-0000-00000000ffff"),
			BodyMarkdown: "First fact.\n\nSecond fact.",
			Claims: new[]
			{
				new WikiClaimDraft(
					ClaimText: "First fact.",
					Position: 0,
					Citations: new[]
					{
						new Citation(Guid.NewGuid(), chunkId, 0, 100, 0.92),
					}),
				new WikiClaimDraft(
					ClaimText: "Second fact.",
					Position: 1,
					Citations: new[]
					{
						new Citation(Guid.NewGuid(), chunkId, 0, 100, 0.85),
					}),
			});

		var revisionId = await writer.CommitAsync(draft);

		revisionId.Should().NotBe(Guid.Empty);

		// Verify the revision + claims + citations all landed and the
		// facet was updated to point at the new revision.
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();

		await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM wiki_claims WHERE revision_id = @r", conn))
		{
			cmd.Parameters.AddWithValue("r", revisionId);
			var count = (long)(await cmd.ExecuteScalarAsync())!;
			count.Should().Be(2);
		}

		await using (var cmd = new NpgsqlCommand(
			"SELECT count(*) FROM wiki_claim_citations wcc INNER JOIN wiki_claims wc ON wc.id = wcc.claim_id WHERE wc.revision_id = @r",
			conn))
		{
			cmd.Parameters.AddWithValue("r", revisionId);
			var count = (long)(await cmd.ExecuteScalarAsync())!;
			count.Should().Be(2);
		}

		await using (var cmd = new NpgsqlCommand(
			"SELECT current_revision_id FROM page_facets WHERE page_id = @p AND min_classification = 'Internal' AND persona_id IS NULL",
			conn))
		{
			cmd.Parameters.AddWithValue("p", pageId);
			var facetRev = (Guid)(await cmd.ExecuteScalarAsync())!;
			facetRev.Should().Be(revisionId);
		}
	}

	[SkippableFact]
	public async Task CommitAsync_Rejects_Duplicate_RevisionNumber_For_Same_Facet()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (pageId, chunkId) = await SeedPageAsync();
		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var writer = new PostgresWikiRevisionWriter(dataSource, NullLogger<PostgresWikiRevisionWriter>.Instance);

		var first = MakeDraft(pageId, chunkId, revno: 1);
		await writer.CommitAsync(first);

		// Same revno for the same facet -> unique-index violation.
		var dup = MakeDraft(pageId, chunkId, revno: 1);
		var act = async () => await writer.CommitAsync(dup);
		await act.Should().ThrowAsync<PostgresException>()
			.Where(ex => ex.SqlState == "23505");
	}

	[SkippableFact]
	public async Task CommitAsync_Throws_When_Facet_Does_Not_Exist()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (_, chunkId) = await SeedPageAsync();

		// Create a fresh page WITHOUT pre-creating the facet row.
		Guid orphanPage;
		await using (var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString))
		{
			await conn.OpenAsync();
			await using var cmd = new NpgsqlCommand("""
				INSERT INTO wiki_pages (department_id, slug, title)
				VALUES (@dept, 'orphan-page', 'Orphan')
				RETURNING id
				""", conn);
			cmd.Parameters.AddWithValue("dept", RlsTestData.EngineeringDeptId);
			orphanPage = (Guid)(await cmd.ExecuteScalarAsync())!;
		}

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var writer = new PostgresWikiRevisionWriter(dataSource, NullLogger<PostgresWikiRevisionWriter>.Instance);

		var draft = MakeDraft(orphanPage, chunkId, revno: 1);
		var act = async () => await writer.CommitAsync(draft);

		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*does not exist*");
	}

	[SkippableFact]
	public async Task CommitAsync_Rolls_Back_On_Citation_Constraint_Violation()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (pageId, _) = await SeedPageAsync();
		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var writer = new PostgresWikiRevisionWriter(dataSource, NullLogger<PostgresWikiRevisionWriter>.Instance);

		// Citation targeting a non-existent chunk -> FK violation.
		// The whole revision should roll back; no orphan rows.
		var unknownChunk = Guid.NewGuid();
		var draft = MakeDraft(pageId, unknownChunk, revno: 1);

		var act = async () => await writer.CommitAsync(draft);
		await act.Should().ThrowAsync<PostgresException>()
			.Where(ex => ex.SqlState == "23503");

		// Confirm no half-state landed.
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand(
			"SELECT count(*) FROM wiki_page_revisions WHERE page_id = @p",
			conn);
		cmd.Parameters.AddWithValue("p", pageId);
		var count = (long)(await cmd.ExecuteScalarAsync())!;
		count.Should().Be(0, "transaction rolled back");
	}

	private static WikiRevisionDraft MakeDraft(Guid pageId, Guid chunkId, int revno) => new(
		PageId: pageId,
		MinClassification: Classification.Internal,
		PersonaId: null,
		RevisionNumber: revno,
		AuthoredBy: new Guid("00000000-0000-0000-0000-00000000ffff"),
		BodyMarkdown: "Body.",
		Claims: new[]
		{
			new WikiClaimDraft(
				ClaimText: "A claim.",
				Position: 0,
				Citations: new[] { new Citation(Guid.NewGuid(), chunkId, 0, 50, 0.9) }),
		});

	private async Task<(Guid PageId, Guid ChunkId)> SeedPageAsync()
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

		// Unique slug per test run to keep parallelism happy.
		var slug = $"wiki-writer-{Guid.NewGuid():N}"[..40].ToLowerInvariant();
		Guid pageId;
		await using (var cmd = new NpgsqlCommand("""
			INSERT INTO wiki_pages (department_id, slug, title)
			VALUES (@dept, @slug, 'Test Page')
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

		return (pageId, chunkId);
	}
}
