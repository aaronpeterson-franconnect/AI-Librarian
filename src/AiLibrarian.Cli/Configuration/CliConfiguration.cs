using Microsoft.Extensions.Configuration;

namespace AiLibrarian.Cli.Configuration;

internal static class CliConfiguration
{
	internal static IConfiguration Build()
	{
		return new ConfigurationBuilder()
			.SetBasePath(AppContext.BaseDirectory)
			.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
			.Build();
	}

	internal static CliAuthOptions GetAuthOptions(IConfiguration configuration)
	{
		var o = new CliAuthOptions();
		configuration.GetSection("Cli:Auth").Bind(o);

		var tenantEnv = Environment.GetEnvironmentVariable("AILIB_TENANT_ID");
		if (!string.IsNullOrWhiteSpace(tenantEnv))
		{
			o.TenantId = tenantEnv.Trim();
		}

		var clientEnv = Environment.GetEnvironmentVariable("AILIB_CLIENT_ID");
		if (!string.IsNullOrWhiteSpace(clientEnv))
		{
			o.ClientId = clientEnv.Trim();
		}

		var scopesEnv = Environment.GetEnvironmentVariable("AILIB_API_SCOPES");
		if (!string.IsNullOrWhiteSpace(scopesEnv))
		{
			o.ApiScopes = scopesEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}

		return o;
	}
}
