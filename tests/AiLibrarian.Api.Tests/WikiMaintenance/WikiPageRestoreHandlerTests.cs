using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AiLibrarian.Domain.Wiki;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Handler-level coverage for
/// <c>POST /api/admin/wiki/pages/{id}/restore</c>. Three-way outcome
/// mapping is the load-bearing thing here: Restored → 200,
/// NotFound → 404, SlugConflict → 409 with the conflicting live
/// page id surfaced so the operator can coordinate the un-delete.
/// </summary>
public sealed class WikiPageRestoreHandlerTests : IClassFixture<WikiMaintenanceWebApplicationFactory>
{
	private readonly WikiMaintenanceWebApplicationFactory _factory;

	public WikiPageRestoreHandlerTests(WikiMaintenanceWebApplicationFactory factory)
	{
		_factory = factory;
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous();
		_factory.PageWrites.RestoreCalls.Clear();
		_factory.PageWrites.ThrowOnRestore = null;
		_factory.PageWrites.RestoreResponder = _ =>
			new RestorePageResult(RestorePageOutcome.Restored, ConflictingLivePageId: null);
	}

	[Fact]
	public async Task Restore_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.PostAsync(
			new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}/restore", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Restore_returns_403_for_authenticated_non_admin()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Librarian(Guid.NewGuid());

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}/restore", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Restore_returns_200_with_restored_outcome_on_success()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var pageId = Guid.NewGuid();

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri($"/api/admin/wiki/pages/{pageId}/restore", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.PageWrites.RestoreCalls.Should().ContainSingle().Which.Should().Be(pageId);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("pageId").GetString().Should().Be(pageId.ToString());
		payload.GetProperty("outcome").GetString().Should().Be("restored");
	}

	[Fact]
	public async Task Restore_returns_404_when_writer_reports_NotFound()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.PageWrites.RestoreResponder = _ =>
			new RestorePageResult(RestorePageOutcome.NotFound, ConflictingLivePageId: null);

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}/restore", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Restore_returns_409_with_conflicting_page_id_on_slug_conflict()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var conflictingId = Guid.NewGuid();
		_factory.PageWrites.RestoreResponder = _ =>
			new RestorePageResult(RestorePageOutcome.SlugConflict, ConflictingLivePageId: conflictingId);

		using var client = _factory.CreateClient();
		var requestedId = Guid.NewGuid();
		var response = await client.PostAsync(
			new Uri($"/api/admin/wiki/pages/{requestedId}/restore", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("pageId").GetString().Should().Be(requestedId.ToString());
		payload.GetProperty("reason").GetString().Should().Be("slug_already_in_use_by_live_page");
		payload.GetProperty("conflictingLivePageId").GetString().Should().Be(conflictingId.ToString());
	}

	[Fact]
	public async Task Restore_returns_503_when_writer_unconfigured()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.PageWrites.ThrowOnRestore = new InvalidOperationException(
			"Wiki page writes require ConnectionStrings:Postgres to be configured.");

		try
		{
			using var client = _factory.CreateClient();
			var response = await client.PostAsync(
				new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}/restore", UriKind.Relative),
				content: null);

			response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		}
		finally
		{
			_factory.PageWrites.ThrowOnRestore = null;
		}
	}
}
