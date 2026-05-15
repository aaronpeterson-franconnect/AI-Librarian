using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Mcp.Auth;

/// <summary>
/// DI registration for the bearer-token provider. Picks the MSAL
/// silent-acquisition provider when <c>Mcp:Auth</c> (with
/// <c>AILIB_*</c> env-var overrides) is fully populated; falls back
/// to <see cref="EnvironmentBearerTokenProvider"/> otherwise so an
/// MCP host that's only handed a static <c>AILIB_ACCESS_TOKEN</c>
/// (e.g. CI smoke tests, container-host scenarios where MSAL isn't
/// available) keeps working.
/// </summary>
public static class BearerTokenProviderServiceCollectionExtensions
{
	/// <summary>Registers the bearer-token provider chosen by config.</summary>
	public static IServiceCollection AddMcpBearerTokenProvider(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		var options = ResolveOptions(configuration);
		services.AddSingleton(Options.Create(options));

		if (options.IsMsalConfigured)
		{
			services.AddSingleton<IBearerTokenProvider, MsalSilentBearerTokenProvider>();
		}
		else
		{
			services.AddSingleton<IBearerTokenProvider, EnvironmentBearerTokenProvider>();
		}

		return services;
	}

	/// <summary>
	/// Build <see cref="McpAuthOptions"/> from <c>Mcp:Auth</c> with
	/// <c>AILIB_*</c> env-var overrides. Mirrors the CLI's
	/// <c>CliConfiguration.GetAuthOptions</c> shape so a single set of
	/// env vars configures both processes.
	/// </summary>
	internal static McpAuthOptions ResolveOptions(IConfiguration configuration)
	{
		var options = new McpAuthOptions();
		configuration.GetSection(McpAuthOptions.SectionName).Bind(options);

		var tenant = Environment.GetEnvironmentVariable("AILIB_TENANT_ID");
		if (!string.IsNullOrWhiteSpace(tenant))
		{
			options.TenantId = tenant.Trim();
		}

		var client = Environment.GetEnvironmentVariable("AILIB_CLIENT_ID");
		if (!string.IsNullOrWhiteSpace(client))
		{
			options.ClientId = client.Trim();
		}

		var scopes = Environment.GetEnvironmentVariable("AILIB_API_SCOPES");
		if (!string.IsNullOrWhiteSpace(scopes))
		{
			options.ApiScopes = scopes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}

		var cacheDir = Environment.GetEnvironmentVariable("AILIB_MSAL_CACHE_DIR");
		if (!string.IsNullOrWhiteSpace(cacheDir))
		{
			options.CacheDirectory = cacheDir.Trim();
		}

		return options;
	}
}
