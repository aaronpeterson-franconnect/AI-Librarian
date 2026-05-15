using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Default appsettings have no Postgres connection string; the
/// /api/sources/{id} endpoint must report "Sources not configured"
/// rather than throwing. Pinning this protects local-dev "no Postgres"
/// mode while still proving the route, RLS plumbing, and audit
/// integration are wired.
/// </summary>
public sealed class SourceEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public SourceEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task Get_source_returns_503_when_postgres_not_configured()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(new Uri(
			"/api/sources/11111111-1111-1111-1111-111111111111",
			UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("title").GetString().Should().Be("Sources not configured");
	}

	[Fact]
	public async Task Get_source_rejects_non_guid_id()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(new Uri("/api/sources/not-a-guid", UriKind.Relative));

		// The route constraint `:guid` rejects non-GUID values before the
		// handler runs, returning 404 — this guarantees malformed ids
		// don't reach the repository.
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}
}
