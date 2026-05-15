using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Default appsettings have no blob connection string — portal upload returns 503.
/// </summary>
public sealed class PortalUploadEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public PortalUploadEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task Portal_upload_returns_503_when_blob_storage_not_configured()
	{
		using var client = _factory.CreateClient();

		using var content = new MultipartFormDataContent();
		var bytes = Encoding.UTF8.GetBytes("# Title\n");
		var fileContent = new ByteArrayContent(bytes);
		fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
		content.Add(fileContent, "file", "note.md");

		var response = await client.PostAsync(new Uri("/api/portal/sources/upload", UriKind.Relative), content);

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

		var body = await response.Content.ReadAsStringAsync();
		// Blob check fires first; even with the new dedup precondition,
		// the blob-storage 503 still wins because it's checked earlier.
		body.Should().Contain("Blob storage not configured");
	}
}
