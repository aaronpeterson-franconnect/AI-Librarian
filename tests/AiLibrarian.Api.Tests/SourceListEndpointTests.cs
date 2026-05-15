using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Default appsettings have no Postgres connection string; the
/// <c>/api/sources</c> list endpoint must report "Sources not configured"
/// rather than empty-200. Pinning this protects local-dev "no Postgres"
/// mode while still proving the route + RLS plumbing are wired.
/// </summary>
public sealed class SourceListEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public SourceListEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task List_sources_returns_503_when_postgres_not_configured()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(new Uri("/api/sources?limit=10", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("title").GetString().Should().Be("Sources not configured");
	}
}
