using AiLibrarian.Domain;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Testcontainer-backed coverage for the auto-page-discovery writer.
/// Pins idempotency: a second call with the same (dept, slug) reuses
/// the existing page; a second call for the same facet reuses the
/// existing facet; calls for different facets on the same page extend
/// the page additively without overwriting anything.
/// </summary>
public sealed class PostgresWikiPageWriterTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresWikiPageWriterTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task EnsurePageAsync_Creates_Page_And_Facet_On_First_Call()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var slug = UniqueSlug();

		var result = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "First Page",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		result.PageCreated.Should().BeTrue();
		result.FacetCreated.Should().BeTrue();
		result.PageId.Should().NotBe(Guid.Empty);

		// Page row exists with the supplied title.
		(await SelectScalarAsync(
			"SELECT title FROM wiki_pages WHERE id = @id",
			("id", result.PageId)))
			.Should().Be("First Page");

		// Facet row exists for (page, Internal, null persona).
		(await SelectScalarAsync<long>(
			"SELECT count(*) FROM page_facets WHERE page_id = @p AND min_classification = 'Internal' AND persona_id IS NULL",
			("p", result.PageId)))
			.Should().Be(1);
	}

	[SkippableFact]
	public async Task EnsurePageAsync_Is_Idempotent_On_Same_Page_And_Facet()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var slug = UniqueSlug();

		var request = new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "First Title",
			FacetClassification: Classification.Internal,
			PersonaId: null);

		var first = await writer.EnsurePageAsync(request);
		var second = await writer.EnsurePageAsync(request with { Title = "Second Title (ignored)" });

		second.PageId.Should().Be(first.PageId);
		second.PageCreated.Should().BeFalse();
		second.FacetCreated.Should().BeFalse();

		// Title was NOT overwritten by the second call. Operators rename
		// pages by other means; discover stays additive.
		(await SelectScalarAsync(
			"SELECT title FROM wiki_pages WHERE id = @id",
			("id", first.PageId)))
			.Should().Be("First Title");

		// Exactly one facet row -- the second call did not duplicate.
		(await SelectScalarAsync<long>(
			"SELECT count(*) FROM page_facets WHERE page_id = @p",
			("p", first.PageId)))
			.Should().Be(1);
	}

	[SkippableFact]
	public async Task EnsurePageAsync_Adds_New_Facet_To_Existing_Page()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var slug = UniqueSlug();

		var first = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Multi-Facet Page",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		// Same page, different classification -- expect a second facet row.
		var second = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Multi-Facet Page",
			FacetClassification: Classification.Confidential,
			PersonaId: null));

		second.PageId.Should().Be(first.PageId);
		second.PageCreated.Should().BeFalse();
		second.FacetCreated.Should().BeTrue();

		(await SelectScalarAsync<long>(
			"SELECT count(*) FROM page_facets WHERE page_id = @p",
			("p", first.PageId)))
			.Should().Be(2);
	}

	[SkippableFact]
	public async Task EnsurePageAsync_Distinguishes_Null_From_NonNull_Persona()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var personaId = await InsertPersonaAsync();

		var writer = CreateWriter();
		var slug = UniqueSlug();

		var neutral = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Persona Page",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		var personaScoped = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Persona Page",
			FacetClassification: Classification.Internal,
			PersonaId: personaId));

		neutral.PageId.Should().Be(personaScoped.PageId);
		personaScoped.PageCreated.Should().BeFalse();
		personaScoped.FacetCreated.Should().BeTrue("the COALESCE-on-PK pattern lets persona-null and persona-specific facets coexist");

		(await SelectScalarAsync<long>(
			"SELECT count(*) FROM page_facets WHERE page_id = @p AND min_classification = 'Internal'",
			("p", neutral.PageId)))
			.Should().Be(2);
	}

	[SkippableFact]
	public async Task RenameAsync_Updates_Title_And_Returns_True()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var slug = UniqueSlug();

		var ensure = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Original Title",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		var renamed = await writer.RenameAsync(ensure.PageId, "Renamed Title");
		renamed.Should().BeTrue();

		(await SelectScalarAsync(
			"SELECT title FROM wiki_pages WHERE id = @id",
			("id", ensure.PageId)))
			.Should().Be("Renamed Title");
	}

	[SkippableFact]
	public async Task RenameAsync_Returns_False_For_Missing_Page()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var renamed = await writer.RenameAsync(Guid.NewGuid(), "Anything");
		renamed.Should().BeFalse();
	}

	[SkippableFact]
	public async Task SetLockedAsync_Flips_Flag_And_Returns_True()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var slug = UniqueSlug();

		var ensure = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Lockable",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		// New page defaults to locked=false (Liquibase 0020 default).
		(await SelectScalarAsync<bool>(
			"SELECT locked FROM wiki_pages WHERE id = @id",
			("id", ensure.PageId)))
			.Should().BeFalse();

		var locked = await writer.SetLockedAsync(ensure.PageId, locked: true);
		locked.Should().BeTrue();

		(await SelectScalarAsync<bool>(
			"SELECT locked FROM wiki_pages WHERE id = @id",
			("id", ensure.PageId)))
			.Should().BeTrue();

		// Idempotent: re-locking returns true (row exists).
		var relocked = await writer.SetLockedAsync(ensure.PageId, locked: true);
		relocked.Should().BeTrue();

		// Unlock back to false.
		var unlocked = await writer.SetLockedAsync(ensure.PageId, locked: false);
		unlocked.Should().BeTrue();
		(await SelectScalarAsync<bool>(
			"SELECT locked FROM wiki_pages WHERE id = @id",
			("id", ensure.PageId)))
			.Should().BeFalse();
	}

	[SkippableFact]
	public async Task SetLockedAsync_Returns_False_For_Missing_Page()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var locked = await writer.SetLockedAsync(Guid.NewGuid(), locked: true);
		locked.Should().BeFalse();
	}

	[Fact]
	public void RenameAsync_Rejects_Empty_PageId()
	{
		var ds = NpgsqlDataSource.Create("Host=ignored;Database=ignored;Username=ignored");
		var writer = new PostgresWikiPageWriter(ds, NullLogger<PostgresWikiPageWriter>.Instance);

		var act = async () => await writer.RenameAsync(Guid.Empty, "anything");
		act.Should().ThrowAsync<ArgumentException>();
	}

	[SkippableFact]
	public async Task SoftDeleteAsync_Sets_Timestamp_And_Returns_True()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var slug = UniqueSlug();

		var ensure = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "To be deleted",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		var deleted = await writer.SoftDeleteAsync(ensure.PageId);
		deleted.Should().BeTrue();

		(await SelectScalarAsync<long>(
			"SELECT count(*) FROM wiki_pages WHERE id = @id AND soft_deleted_at IS NOT NULL",
			("id", ensure.PageId)))
			.Should().Be(1);
	}

	[SkippableFact]
	public async Task SoftDeleteAsync_Returns_False_For_Missing_Page()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		(await writer.SoftDeleteAsync(Guid.NewGuid())).Should().BeFalse();
	}

	[SkippableFact]
	public async Task SoftDeleteAsync_Is_Idempotent_Second_Call_Returns_False()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var ensure = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: UniqueSlug(),
			Title: "Once-deleted",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		(await writer.SoftDeleteAsync(ensure.PageId)).Should().BeTrue();
		(await writer.SoftDeleteAsync(ensure.PageId)).Should().BeFalse(
			"re-deleting an already-soft-deleted row is a no-op; the WHERE soft_deleted_at IS NULL predicate filters it out");
	}

	[SkippableFact]
	public async Task EnsurePageAsync_Reuses_Slug_After_SoftDelete()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var slug = UniqueSlug();

		// Create then soft-delete the original page.
		var original = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Original",
			FacetClassification: Classification.Internal,
			PersonaId: null));
		(await writer.SoftDeleteAsync(original.PageId)).Should().BeTrue();

		// Re-create with the same slug; the partial unique index lets
		// this through. The new row has a fresh id.
		var recreated = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Recreated",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		recreated.PageCreated.Should().BeTrue("the live partial-unique index allows slug reuse after soft-delete");
		recreated.PageId.Should().NotBe(original.PageId, "the new row has a fresh id");
	}

	[SkippableFact]
	public async Task RenameAsync_Skips_SoftDeleted_Rows()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var ensure = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: UniqueSlug(),
			Title: "Pre-delete",
			FacetClassification: Classification.Internal,
			PersonaId: null));
		await writer.SoftDeleteAsync(ensure.PageId);

		(await writer.RenameAsync(ensure.PageId, "After-delete")).Should().BeFalse(
			"rename should not touch soft-deleted rows; the WHERE predicate excludes them");
	}

	[SkippableFact]
	public async Task SetLockedAsync_Skips_SoftDeleted_Rows()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var ensure = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: UniqueSlug(),
			Title: "Lock then delete",
			FacetClassification: Classification.Internal,
			PersonaId: null));
		await writer.SoftDeleteAsync(ensure.PageId);

		(await writer.SetLockedAsync(ensure.PageId, locked: true)).Should().BeFalse();
	}

	[SkippableFact]
	public async Task RestoreAsync_Restores_SoftDeleted_Page()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var ensure = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: UniqueSlug(),
			Title: "Restore me",
			FacetClassification: Classification.Internal,
			PersonaId: null));
		await writer.SoftDeleteAsync(ensure.PageId);

		var result = await writer.RestoreAsync(ensure.PageId);

		result.Outcome.Should().Be(RestorePageOutcome.Restored);
		result.ConflictingLivePageId.Should().BeNull();

		(await SelectScalarAsync<long>(
			"SELECT count(*) FROM wiki_pages WHERE id = @id AND soft_deleted_at IS NULL",
			("id", ensure.PageId)))
			.Should().Be(1, "the row is live again");
	}

	[SkippableFact]
	public async Task RestoreAsync_Returns_NotFound_For_Unknown_Page()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var result = await writer.RestoreAsync(Guid.NewGuid());

		result.Outcome.Should().Be(RestorePageOutcome.NotFound);
	}

	[SkippableFact]
	public async Task RestoreAsync_Returns_NotFound_For_Already_Live_Page()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var ensure = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: UniqueSlug(),
			Title: "Already live",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		// Not soft-deleted -> restore returns NotFound (no soft-deleted
		// row to clear).
		var result = await writer.RestoreAsync(ensure.PageId);

		result.Outcome.Should().Be(RestorePageOutcome.NotFound);
	}

	[SkippableFact]
	public async Task RestoreAsync_Returns_SlugConflict_When_Live_Row_Holds_Slug()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var slug = UniqueSlug();

		// Create + soft-delete + recreate the slug under a new id.
		var original = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Original",
			FacetClassification: Classification.Internal,
			PersonaId: null));
		await writer.SoftDeleteAsync(original.PageId);
		var replacement = await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Slug: slug,
			Title: "Replacement",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		// Now attempt to restore the original -- the replacement holds
		// the slug, so the partial unique index would block. The
		// writer detects this and returns SlugConflict with the
		// replacement's id.
		var result = await writer.RestoreAsync(original.PageId);

		result.Outcome.Should().Be(RestorePageOutcome.SlugConflict);
		result.ConflictingLivePageId.Should().Be(replacement.PageId);

		// The original is still soft-deleted (the conflict path is a
		// no-op write).
		(await SelectScalarAsync<long>(
			"SELECT count(*) FROM wiki_pages WHERE id = @id AND soft_deleted_at IS NOT NULL",
			("id", original.PageId)))
			.Should().Be(1);
	}

	[Fact]
	public void EnsurePageAsync_Rejects_Invalid_Slug()
	{
		// No DB needed; ArgumentException fires before we open a connection.
		var dataSource = NpgsqlDataSource.Create("Host=ignored;Database=ignored;Username=ignored");
		var writer = new PostgresWikiPageWriter(dataSource, NullLogger<PostgresWikiPageWriter>.Instance);

		var act = async () => await writer.EnsurePageAsync(new EnsurePageRequest(
			DepartmentId: Guid.NewGuid(),
			Slug: "INVALID SLUG",
			Title: "Title",
			FacetClassification: Classification.Internal,
			PersonaId: null));

		act.Should().ThrowAsync<ArgumentException>();
	}

	// --- helpers ---

	private PostgresWikiPageWriter CreateWriter()
	{
		var ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
		return new PostgresWikiPageWriter(ds, NullLogger<PostgresWikiPageWriter>.Instance);
	}

	private static string UniqueSlug()
		=> $"discover-{Guid.NewGuid():N}"[..40].ToLowerInvariant();

	private async Task<string?> SelectScalarAsync(string sql, params (string Name, object Value)[] parameters)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand(sql, conn);
		foreach (var (n, v) in parameters)
		{
			cmd.Parameters.AddWithValue(n, v);
		}
		return (await cmd.ExecuteScalarAsync()) as string;
	}

	private async Task<T> SelectScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
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

	private async Task<Guid> InsertPersonaAsync()
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO personas (name, display_name, description)
			VALUES (@name, @display, @desc)
			RETURNING id
			""", conn);
		var name = $"persona-{Guid.NewGuid():N}"[..40].ToLowerInvariant();
		cmd.Parameters.AddWithValue("name", name);
		cmd.Parameters.AddWithValue("display", "Test persona");
		cmd.Parameters.AddWithValue("desc", "Auto-page-discovery test fixture.");
		return (Guid)(await cmd.ExecuteScalarAsync())!;
	}
}
