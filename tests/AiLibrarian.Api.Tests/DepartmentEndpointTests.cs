using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Default appsettings have no Postgres connection string; the
/// /api/departments endpoint must report "Departments not configured"
/// rather than empty-200 — operators need to distinguish "no
/// departments visible" from "Postgres unreachable".
/// </summary>
public sealed class DepartmentEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public DepartmentEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task List_departments_returns_503_when_postgres_not_configured()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(new Uri("/api/departments", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("title").GetString().Should().Be("Departments not configured");
	}
}
