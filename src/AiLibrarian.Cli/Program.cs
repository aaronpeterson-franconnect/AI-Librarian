using System.CommandLine;

using AiLibrarian.Cli.Auth;
using AiLibrarian.Cli.Configuration;
using AiLibrarian.Cli.Mcp;
using AiLibrarian.Cli.Security;

using Microsoft.Extensions.Configuration;

var configuration = CliConfiguration.Build();

var root = new RootCommand("AI Librarian CLI - Entra sign-in and stdio MCP bridge (Phase 1).");

var login = new Command("login", "Sign in with Microsoft Entra using device code (public client).");
login.SetHandler(async () =>
{
	var code = await EntraPublicClient.LoginAsync(configuration, CancellationToken.None).ConfigureAwait(false);
	Environment.Exit(code);
});

var logout = new Command("logout", "Remove cached Entra accounts for this CLI.");
logout.SetHandler(async () =>
{
	var code = await EntraPublicClient.LogoutAsync(configuration, CancellationToken.None).ConfigureAwait(false);
	Environment.Exit(code);
});

var mcpDllOption = new Option<string?>("--mcp-dll", "Path to AiLibrarian.Mcp.dll (optional if copied next to ailib or set AILIB_MCP_DLL).");
var mcp = new Command("mcp", "Run the MCP server on stdio (for IDE MCP config). Forwards AILIB_ACCESS_TOKEN when signed in and Api__BaseUrl when AILIB_API_BASE_URL or Cli:Api:BaseUrl is set.")
{
	mcpDllOption,
};
mcp.SetHandler(async (string? mcpDll) =>
{
	var path = McpHostRunner.ResolveMcpDllPath(mcpDll);
	if (path is null)
	{
		Console.Error.WriteLine(
			"Could not find AiLibrarian.Mcp.dll. Build the solution, use --mcp-dll, or set AILIB_MCP_DLL.");
		Environment.Exit(3);
		return;
	}

	var token = await EntraPublicClient.TryGetAccessTokenSilentAsync(configuration, CancellationToken.None)
		.ConfigureAwait(false);
	if (string.IsNullOrEmpty(token))
	{
		Console.Error.WriteLine("No cached token. Run \"ailib login\" first (or check Cli:Auth / AILIB_* env).");
		Environment.Exit(2);
		return;
	}

	var env = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		// Initial bearer token — saves a first-call MSAL round-trip in the
		// child process. The MCP server's token provider refreshes from
		// MSAL silently after this initial value expires.
		["AILIB_ACCESS_TOKEN"] = token,
	};

	// Forward MSAL config so the MCP host can self-refresh (B.8). Without
	// these, the child falls back to the static AILIB_ACCESS_TOKEN env var
	// which expires after ~1 hour and breaks long-lived stdio sessions.
	var authOptions = CliConfiguration.GetAuthOptions(configuration);
	if (!string.IsNullOrWhiteSpace(authOptions.TenantId))
	{
		env["AILIB_TENANT_ID"] = authOptions.TenantId;
	}

	if (!string.IsNullOrWhiteSpace(authOptions.ClientId))
	{
		env["AILIB_CLIENT_ID"] = authOptions.ClientId;
	}

	if (authOptions.ApiScopes.Length > 0)
	{
		env["AILIB_API_SCOPES"] = string.Join(';', authOptions.ApiScopes);
	}

	var apiBaseUrl = Environment.GetEnvironmentVariable("AILIB_API_BASE_URL");
	if (string.IsNullOrWhiteSpace(apiBaseUrl))
	{
		apiBaseUrl = configuration["Cli:Api:BaseUrl"];
	}

	if (!string.IsNullOrWhiteSpace(apiBaseUrl))
	{
		env["Api__BaseUrl"] = apiBaseUrl.Trim();
	}

	Environment.Exit(McpHostRunner.Run(path, env));
}, mcpDllOption);

root.AddCommand(login);
root.AddCommand(logout);
root.AddCommand(mcp);
root.AddCommand(PrecisionSamplingCommand.Build());

return await root.InvokeAsync(args).ConfigureAwait(false);
