using AiLibrarian.Cli.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AiLibrarian.Cli.Auth;

/// <summary>MSAL public client + encrypted file cache (Extensions.Msal) for workstation sign-in.</summary>
internal static class EntraPublicClient
{
	private static readonly string CacheDirectory = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"AiLibrarian",
		"Cli");

	internal static IPublicClientApplication CreateApp(CliAuthOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		return PublicClientApplicationBuilder
			.Create(options.ClientId)
			.WithTenantId(options.TenantId)
			.Build();
	}

	internal static async Task<MsalCacheHelper> CreateCacheHelperAsync(CliAuthOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		Directory.CreateDirectory(CacheDirectory);

		var storage = new StorageCreationPropertiesBuilder("msal_cache_v3.bin", CacheDirectory)
			.Build();

		return await MsalCacheHelper.CreateAsync(storage, logger: null).ConfigureAwait(false);
	}

	internal static void RegisterCache(MsalCacheHelper helper, IPublicClientApplication app)
	{
		ArgumentNullException.ThrowIfNull(helper);
		ArgumentNullException.ThrowIfNull(app);
		helper.RegisterCache(app.UserTokenCache);
	}

	internal static async Task<int> LoginAsync(IConfiguration configuration, CancellationToken cancellationToken)
	{
		var options = CliConfiguration.GetAuthOptions(configuration);
		if (string.IsNullOrWhiteSpace(options.TenantId) || string.IsNullOrWhiteSpace(options.ClientId))
		{
			Console.Error.WriteLine("Set Cli:Auth:TenantId and Cli:Auth:ClientId in appsettings.json, or AILIB_TENANT_ID / AILIB_CLIENT_ID.");
			return 2;
		}

		if (options.ApiScopes.Length == 0)
		{
			Console.Error.WriteLine("Set Cli:Auth:ApiScopes (JSON array) or AILIB_API_SCOPES (semicolon-separated), e.g. api://<your-api-app-id>/.default");
			return 2;
		}

		var app = CreateApp(options);
		var cacheHelper = await CreateCacheHelperAsync(options).ConfigureAwait(false);
		RegisterCache(cacheHelper, app);

		var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
		var account = accounts.FirstOrDefault();
		if (account is not null)
		{
			try
			{
				await app.AcquireTokenSilent(options.ApiScopes, account)
					.ExecuteAsync(cancellationToken)
					.ConfigureAwait(false);
				Console.WriteLine("Already signed in; token refreshed silently.");
				return 0;
			}
			catch (MsalUiRequiredException)
			{
				// continue to device code
			}
		}

		await app.AcquireTokenWithDeviceCode(options.ApiScopes, dcr =>
			{
				Console.WriteLine(dcr.Message);
				return Task.CompletedTask;
			})
			.ExecuteAsync(cancellationToken)
			.ConfigureAwait(false);

		Console.WriteLine("Signed in. Token cache stored under LocalApplicationData/AiLibrarian/Cli.");
		return 0;
	}

	internal static async Task<int> LogoutAsync(IConfiguration configuration, CancellationToken cancellationToken)
	{
		var options = CliConfiguration.GetAuthOptions(configuration);
		if (string.IsNullOrWhiteSpace(options.TenantId) || string.IsNullOrWhiteSpace(options.ClientId))
		{
			Console.Error.WriteLine("Set Cli:Auth:TenantId and Cli:Auth:ClientId (or env overrides) before logout.");
			return 2;
		}

		var app = CreateApp(options);
		var cacheHelper = await CreateCacheHelperAsync(options).ConfigureAwait(false);
		RegisterCache(cacheHelper, app);

		var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
		foreach (var account in accounts)
		{
			await app.RemoveAsync(account).ConfigureAwait(false);
		}

		Console.WriteLine("Signed out; removed cached accounts.");
		return 0;
	}

	internal static async Task<string?> TryGetAccessTokenSilentAsync(
		IConfiguration configuration,
		CancellationToken cancellationToken)
	{
		var options = CliConfiguration.GetAuthOptions(configuration);
		if (options.ApiScopes.Length == 0 || string.IsNullOrWhiteSpace(options.ClientId))
		{
			return null;
		}

		var app = CreateApp(options);
		var cacheHelper = await CreateCacheHelperAsync(options).ConfigureAwait(false);
		RegisterCache(cacheHelper, app);

		var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
		var first = accounts.FirstOrDefault();
		if (first is null)
		{
			return null;
		}

		try
		{
			var result = await app.AcquireTokenSilent(options.ApiScopes, first)
				.ExecuteAsync(cancellationToken)
				.ConfigureAwait(false);
			return result.AccessToken;
		}
		catch (MsalUiRequiredException)
		{
			return null;
		}
	}
}
