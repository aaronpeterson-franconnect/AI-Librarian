using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Default appsettings have no Postgres connection string — hybrid search returns 503 before embeddings.
/// </summary>
public sealed class HybridSearchEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public HybridSearchEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task Hybrid_search_returns_503_when_postgres_not_configured()
	{
		using var client = _factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			new Uri("/api/search/hybrid", UriKind.Relative),
			new { query = "secrets rotation" });

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("title").GetString().Should().Be("Retrieval not configured");
	}
}
