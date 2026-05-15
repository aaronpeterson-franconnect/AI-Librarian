using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiLibrarian.Api.Tests;

/// <summary>
/// Default appsettings have no Postgres connection string; the
/// <c>/api/audit/recent</c> endpoint must report "Audit query not
/// configured" rather than empty-200 — operators need to distinguish
/// "audit ledger empty" from "Postgres unreachable / NoOp writer mode".
/// </summary>
public sealed class AuditRecentEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public AuditRecentEndpointTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory.WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Testing");
		});
	}

	[Fact]
	public async Task List_recent_audit_returns_503_when_postgres_not_configured()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(new Uri("/api/audit/recent?limit=10", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("title").GetString().Should().Be("Audit query not configured");
	}
}
