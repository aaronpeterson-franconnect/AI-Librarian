using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Wire-up smoke for the auto-page-discovery endpoint
/// (<c>POST /api/admin/wiki/discover</c>). The Testing environment has
/// no Postgres, which means the route handler's
/// <c>WikiRevisionNumberer</c> dependency cannot be activated — same as
/// the existing <c>/api/admin/wiki/maintain</c> endpoint. Behavioural
/// coverage lives in
/// <c>PostgresWikiPageWriterTests</c> (idempotent upsert under a real
/// container) and <c>WikiSlugTests</c> (helper used by the handler).
///
/// <para>This test asserts only that the route is in the route table:
/// ASP.NET routing returns 405 for a wrong-method match BEFORE parameter
/// binding / DI activation, so we can confirm wire-up without booting
/// the full handler.</para>
/// </summary>
public sealed class WikiDiscoverEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public WikiDiscoverEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task Discover_endpoint_is_mapped_under_the_admin_route()
	{
		using var client = _factory.CreateClient();

		// GET to a POST-only route must produce 405. 404 would mean we
		// typo'd the path or forgot to map it.
		var response = await client.GetAsync(new Uri("/api/admin/wiki/discover", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
	}

	[Fact]
	public async Task PagePatch_endpoint_is_mapped_under_the_admin_route()
	{
		using var client = _factory.CreateClient();

		// GET to a PATCH-only route must produce 405.
		var response = await client.GetAsync(new Uri($"/api/admin/wiki/pages/{Guid.NewGuid()}", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
	}

	[Fact]
	public async Task ProposedDecisions_endpoint_is_mapped_under_the_admin_route()
	{
		using var client = _factory.CreateClient();

		// PUT to a GET-only route must produce 405. The handler itself
		// requires Postgres (proposal reader) and would 503 in Testing
		// env if we sent GET, so we use a wrong-method probe to confirm
		// wire-up without hitting the dependency graph.
		var response = await client.PutAsync(
			new Uri("/api/admin/wiki/proposed/decisions", UriKind.Relative),
			new StringContent(""));

		response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
	}

	[Fact]
	public async Task ProposedBulkReject_endpoint_is_mapped_under_the_admin_route()
	{
		using var client = _factory.CreateClient();

		// GET to a POST-only route must produce 405.
		var response = await client.GetAsync(
			new Uri("/api/admin/wiki/proposed/bulk-reject", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
	}

	[Fact]
	public async Task DiscoverCandidates_endpoint_is_mapped_under_the_admin_route()
	{
		using var client = _factory.CreateClient();

		// GET to a POST-only route must produce 405.
		var response = await client.GetAsync(
			new Uri("/api/admin/wiki/discover-candidates", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
	}
}
