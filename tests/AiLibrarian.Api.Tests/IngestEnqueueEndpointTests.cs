using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Default appsettings have no Service Bus connection string — enqueue returns 503 before publish.
/// </summary>
public sealed class IngestEnqueueEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public IngestEnqueueEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task Ingest_enqueue_returns_503_when_service_bus_not_configured()
	{
		using var client = _factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			new Uri("/api/ingest/enqueue", UriKind.Relative),
			new
			{
				blobUri = "https://example.blob.core.windows.net/container/doc.pdf",
			});

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("title").GetString().Should().Be("Ingest queue not configured");
	}
}
