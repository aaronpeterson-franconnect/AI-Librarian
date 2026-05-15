using AiLibrarian.Mcp.Auth;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiLibrarian.Mcp.Tests.Auth;

/// <summary>
/// Pin the DI selection logic — the wrong choice silently breaks
/// long-lived MCP sessions (env-provider when MSAL config is set
/// means tokens never refresh) or breaks env-var-only deployments
/// (MSAL provider with no config tries to acquire against an empty
/// account list and returns null forever).
/// </summary>
[Collection("EnvironmentMutation")]
public sealed class BearerTokenProviderRegistrationTests : IDisposable
{
	private readonly string? _originalTenant;
	private readonly string? _originalClient;
	private readonly string? _originalScopes;
	private readonly string? _originalCacheDir;

	public BearerTokenProviderRegistrationTests()
	{
		_originalTenant = Environment.GetEnvironmentVariable("AILIB_TENANT_ID");
		_originalClient = Environment.GetEnvironmentVariable("AILIB_CLIENT_ID");
		_originalScopes = Environment.GetEnvironmentVariable("AILIB_API_SCOPES");
		_originalCacheDir = Environment.GetEnvironmentVariable("AILIB_MSAL_CACHE_DIR");

		Environment.SetEnvironmentVariable("AILIB_TENANT_ID", null);
		Environment.SetEnvironmentVariable("AILIB_CLIENT_ID", null);
		Environment.SetEnvironmentVariable("AILIB_API_SCOPES", null);
		Environment.SetEnvironmentVariable("AILIB_MSAL_CACHE_DIR", null);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("AILIB_TENANT_ID", _originalTenant);
		Environment.SetEnvironmentVariable("AILIB_CLIENT_ID", _originalClient);
		Environment.SetEnvironmentVariable("AILIB_API_SCOPES", _originalScopes);
		Environment.SetEnvironmentVariable("AILIB_MSAL_CACHE_DIR", _originalCacheDir);
	}

	[Fact]
	public void Falls_back_to_environment_provider_when_msal_config_missing()
	{
		var configuration = new ConfigurationBuilder().Build();
		var services = new ServiceCollection();
		services.AddLogging();

		services.AddMcpBearerTokenProvider(configuration);

		using var sp = services.BuildServiceProvider();
		var provider = sp.GetRequiredService<IBearerTokenProvider>();
		provider.Should().BeOfType<EnvironmentBearerTokenProvider>();
	}

	[Fact]
	public void Selects_msal_provider_when_config_section_populated()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Mcp:Auth:TenantId"] = "11111111-1111-1111-1111-111111111111",
				["Mcp:Auth:ClientId"] = "22222222-2222-2222-2222-222222222222",
				["Mcp:Auth:ApiScopes:0"] = "api://example/.default",
			})
			.Build();

		var services = new ServiceCollection();
		services.AddLogging();

		services.AddMcpBearerTokenProvider(configuration);

		using var sp = services.BuildServiceProvider();
		var provider = sp.GetRequiredService<IBearerTokenProvider>();
		provider.Should().BeOfType<MsalSilentBearerTokenProvider>();
	}

	[Fact]
	public void Env_var_overrides_promote_to_msal_provider()
	{
		Environment.SetEnvironmentVariable("AILIB_TENANT_ID", "11111111-1111-1111-1111-111111111111");
		Environment.SetEnvironmentVariable("AILIB_CLIENT_ID", "22222222-2222-2222-2222-222222222222");
		Environment.SetEnvironmentVariable("AILIB_API_SCOPES", "api://example/.default");

		var configuration = new ConfigurationBuilder().Build();
		var services = new ServiceCollection();
		services.AddLogging();

		services.AddMcpBearerTokenProvider(configuration);

		using var sp = services.BuildServiceProvider();
		var provider = sp.GetRequiredService<IBearerTokenProvider>();
		provider.Should().BeOfType<MsalSilentBearerTokenProvider>();
	}

	[Fact]
	public void Partial_config_falls_back_to_environment_provider()
	{
		// Tenant + client without scopes is incomplete; fall back rather
		// than crash MSAL with an empty scopes array.
		Environment.SetEnvironmentVariable("AILIB_TENANT_ID", "11111111-1111-1111-1111-111111111111");
		Environment.SetEnvironmentVariable("AILIB_CLIENT_ID", "22222222-2222-2222-2222-222222222222");

		var configuration = new ConfigurationBuilder().Build();
		var services = new ServiceCollection();
		services.AddLogging();

		services.AddMcpBearerTokenProvider(configuration);

		using var sp = services.BuildServiceProvider();
		var provider = sp.GetRequiredService<IBearerTokenProvider>();
		provider.Should().BeOfType<EnvironmentBearerTokenProvider>();
	}
}
