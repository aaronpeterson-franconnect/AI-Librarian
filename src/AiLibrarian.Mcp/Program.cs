using AiLibrarian.Mcp;
using AiLibrarian.Mcp.Auth;
using AiLibrarian.Mcp.Internal;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<McpApiOptions>(builder.Configuration.GetSection(McpApiOptions.SectionName));

// Bearer-token provider — picks MSAL silent acquisition (auto-refresh) when
// Mcp:Auth + AILIB_* env vars are populated, otherwise falls back to reading
// the static AILIB_ACCESS_TOKEN env var. Phase 1 long-lived stdio sessions
// stay authenticated via the shared `ailib login` cache (B.8).
builder.Services.AddMcpBearerTokenProvider(builder.Configuration);

builder.Services.AddHttpClient<AiLibrarianApiClient>((sp, client) =>
{
	var opt = sp.GetRequiredService<IOptions<McpApiOptions>>().Value;
	var baseUrl = opt.BaseUrl?.Trim();
	if (!string.IsNullOrEmpty(baseUrl))
	{
		if (!baseUrl.EndsWith('/'))
		{
			baseUrl += "/";
		}

		client.BaseAddress = new Uri(baseUrl);
	}
});

builder.Services.AddLogging();
builder.Services.AddSingleton(McpWorkstationContext.FromEnvironment());
builder.Services.AddHostedService<McpAuthDiagnosticsHostedService>();
builder.Services
	.AddMcpServer()
	.WithStdioServerTransport()
	.WithTools<LibrarianMcpTools>();
await builder.Build().RunAsync().ConfigureAwait(false);
