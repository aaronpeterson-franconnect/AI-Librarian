using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Handler-level coverage for <c>DELETE /api/admin/wiki/pages/{id}</c>
/// (ADR 0008 soft-delete). Tests the Admin gate, the 404-on-missing
/// path (covers both "page doesn't exist" and "already soft-deleted"
/// since the writer returns false in both cases), the success shape,
/// and the 503 fallback for unconfigured Postgres.
/// </summary>
public sealed class WikiPageDeleteHandlerTests : IClassFixture<WikiMaintenanceWebApplicationFactory>
{
	private readonly WikiMaintenanceWebApplicationFactory _factory;

	public WikiPageDeleteHandlerTests(WikiMaintenanceWebApplicationFactory factory)
	{
		_factory = factory;
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous();
		_factory.PageWrites.SoftDeleteCalls.Clear();
		_factory.PageWrites.SoftDeleteReturns = true;
		_factory.PageWrites.ThrowOnSoftDelete = null;
	}

	[Fact]
	public async Task Delete_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.DeleteAsync(
			new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Delete_returns_403_for_authenticated_non_admin()
	{
		// A Librarian on the page's department is NOT enough for delete;
		// the rename/lock surface is also Admin-only and delete is the
		// same gate.
		_factory.Sessions.Current = StubSessionContextResolver.Librarian(Guid.NewGuid());

		using var client = _factory.CreateClient();
		var response = await client.DeleteAsync(
			new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Delete_success_calls_writer_and_returns_outcome()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var pageId = Guid.NewGuid();

		using var client = _factory.CreateClient();
		var response = await client.DeleteAsync(
			new Uri($"/api/admin/wiki/pages/{pageId}", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.PageWrites.SoftDeleteCalls.Should().ContainSingle()
			.Which.Should().Be(pageId);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("pageId").GetString().Should().Be(pageId.ToString());
		payload.GetProperty("softDeleted").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Delete_returns_404_when_writer_reports_not_transitioned()
	{
		// Writer returns false for missing-or-already-deleted. The
		// handler maps both to 404 without exposing the distinction.
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.PageWrites.SoftDeleteReturns = false;

		try
		{
			using var client = _factory.CreateClient();
			var response = await client.DeleteAsync(
				new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative));

			response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		}
		finally
		{
			_factory.PageWrites.SoftDeleteReturns = true;
		}
	}

	[Fact]
	public async Task Delete_returns_503_when_writer_unconfigured()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.PageWrites.ThrowOnSoftDelete = new InvalidOperationException(
			"Wiki page writes require ConnectionStrings:Postgres to be configured.");

		try
		{
			using var client = _factory.CreateClient();
			var response = await client.DeleteAsync(
				new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative));

			response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		}
		finally
		{
			_factory.PageWrites.ThrowOnSoftDelete = null;
		}
	}

	[Fact]
	public async Task Delete_endpoint_is_only_a_delete_route()
	{
		// GET / POST against the same path must NOT trigger soft-delete.
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var pageId = Guid.NewGuid();

		var get = await client.GetAsync(new Uri($"/api/admin/wiki/pages/{pageId}", UriKind.Relative));
		get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

		_factory.PageWrites.SoftDeleteCalls.Should().BeEmpty(
			"GET must not route through the delete handler");
	}
}
