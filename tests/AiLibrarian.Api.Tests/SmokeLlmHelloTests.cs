using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Phase 0: /api/smoke/llm/hello is reachable; with default test settings
/// (Azure OpenAI disabled) it returns 503 — the full audited round-trip is
/// validated manually or in an environment with keys and LlmGateway enabled.
/// </summary>
public sealed class SmokeLlmHelloTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public SmokeLlmHelloTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task Smoke_llm_hello_returns_503_when_gateway_not_configured()
	{
		using var client = _factory.CreateClient();

		var response = await client.PostAsync(new Uri("/api/smoke/llm/hello", UriKind.Relative), null);

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("title").GetString().Should().Be("LLM gateway not configured");
	}
}
