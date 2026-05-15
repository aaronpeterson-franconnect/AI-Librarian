using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Phase 0 smoke test: the /health endpoint returns 200 OK with the
/// expected shape, with no Entra configuration present (i.e., the
/// API boots cleanly in "no Entra" local-dev mode).
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public HealthEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task Health_returns_200_with_expected_shape()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("status").GetString().Should().Be("ok");
		payload.GetProperty("service").GetString().Should().Be("ai-librarian-api");
		payload.GetProperty("entraConfigured").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public async Task BuildInfo_returns_200_with_defaults()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(new Uri("/build-info", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("buildId").GetString().Should().Be("local");
		payload.GetProperty("commit").GetString().Should().Be("unknown");
	}
}
