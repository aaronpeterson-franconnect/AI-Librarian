using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Handler-level coverage for <c>PATCH /api/admin/wiki/pages/{id}</c>.
/// Tests the auth gate, body validation (at least one of title /
/// locked must be set), the 404 case when the page doesn't exist,
/// and the per-field audit-row split (title and locked emit separate
/// audit subtypes — covered indirectly via the writer-call shape).
/// </summary>
public sealed class WikiPagePatchHandlerTests : IClassFixture<WikiMaintenanceWebApplicationFactory>
{
	private readonly WikiMaintenanceWebApplicationFactory _factory;

	public WikiPagePatchHandlerTests(WikiMaintenanceWebApplicationFactory factory)
	{
		_factory = factory;
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous();
		_factory.PageWrites.RenameCalls.Clear();
		_factory.PageWrites.LockCalls.Clear();
		_factory.PageWrites.RenameReturns = true;
		_factory.PageWrites.LockReturns = true;
	}

	[Fact]
	public async Task Patch_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.PatchAsync(
			new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative),
			JsonContent.Create(new { title = "Renamed" }));

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Patch_returns_400_when_neither_title_nor_locked_set()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PatchAsync(
			new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative),
			JsonContent.Create(new { }));

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Patch_returns_400_when_title_is_only_whitespace()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PatchAsync(
			new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative),
			JsonContent.Create(new { title = "   " }));

		// title is whitespace AND locked is null -> validation says "at least one of"
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Patch_rename_only_calls_rename_not_lock()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var pageId = Guid.NewGuid();

		using var client = _factory.CreateClient();
		var response = await client.PatchAsync(
			new Uri($"/api/admin/wiki/pages/{pageId}", UriKind.Relative),
			JsonContent.Create(new { title = "New Title" }));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.PageWrites.RenameCalls.Should().ContainSingle()
			.Which.NewTitle.Should().Be("New Title");
		_factory.PageWrites.LockCalls.Should().BeEmpty();

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("titleUpdated").GetBoolean().Should().BeTrue();
		payload.GetProperty("lockedUpdated").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public async Task Patch_lock_only_calls_setlocked_not_rename()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var pageId = Guid.NewGuid();

		using var client = _factory.CreateClient();
		var response = await client.PatchAsync(
			new Uri($"/api/admin/wiki/pages/{pageId}", UriKind.Relative),
			JsonContent.Create(new { locked = true }));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.PageWrites.LockCalls.Should().ContainSingle()
			.Which.Locked.Should().BeTrue();
		_factory.PageWrites.RenameCalls.Should().BeEmpty();
	}

	[Fact]
	public async Task Patch_combined_rename_and_lock_calls_both_writers()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var pageId = Guid.NewGuid();

		using var client = _factory.CreateClient();
		var response = await client.PatchAsync(
			new Uri($"/api/admin/wiki/pages/{pageId}", UriKind.Relative),
			JsonContent.Create(new { title = "Renamed", locked = false }));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.PageWrites.RenameCalls.Should().HaveCount(1);
		_factory.PageWrites.LockCalls.Should().HaveCount(1);
	}

	[Fact]
	public async Task Patch_returns_404_when_writer_reports_no_row_updated()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.PageWrites.RenameReturns = false;
		_factory.PageWrites.LockReturns = false;
		try
		{
			using var client = _factory.CreateClient();
			var response = await client.PatchAsync(
				new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative),
				JsonContent.Create(new { title = "Renamed" }));

			response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		}
		finally
		{
			_factory.PageWrites.RenameReturns = true;
			_factory.PageWrites.LockReturns = true;
		}
	}

	[Fact]
	public async Task Patch_returns_503_when_writer_unconfigured()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		// Replace the writer's rename response with a throw to simulate
		// the NullWikiPageWriter "Postgres not configured" path.
		var originalEnsure = _factory.PageWrites.EnsureResponse;
		_factory.PageWrites.RenameReturns = true; // unused -- we throw before reading
		var hadException = false;
		try
		{
			// We need RenameAsync itself to throw. Easiest: replace the stub
			// via a temporary delegate field. The factory's stub doesn't
			// expose that, so do it via a quick reflection trick.
			// Simpler: assert the 503 path with a fresh in-test stub. The
			// existing stub doesn't natively support throwing on rename --
			// add via a flag for now.
			_factory.PageWrites.ThrowOnRename = new InvalidOperationException("Wiki page writes require ConnectionStrings:Postgres to be configured.");

			using var client = _factory.CreateClient();
			var response = await client.PatchAsync(
				new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative),
				JsonContent.Create(new { title = "Renamed" }));

			response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
			hadException = true;
		}
		finally
		{
			_factory.PageWrites.ThrowOnRename = null;
		}
		hadException.Should().BeTrue();
	}
}
