using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AiLibrarian.Mcp.Auth;

/// <summary>
/// Resolves bearer tokens via MSAL silent acquisition against the
/// shared <c>ailib login</c> cache. <c>IPublicClientApplication.AcquireTokenSilent</c>
/// transparently exchanges a refresh token for a fresh access token
/// when the cached one is near or past expiry — that's the whole
/// point of this provider. Long-lived stdio MCP sessions stay
/// authenticated as long as the refresh token is valid (Entra default:
/// 90 days for a typical refresh).
///
/// <para>
/// On <see cref="MsalUiRequiredException"/> (refresh token expired,
/// account removed, conditional-access challenge), returns
/// <see langword="null"/>. The caller's API request goes out without
/// a bearer; the API responds 401 and the MCP tool surface translates
/// that into a typed error the AI client can show ("please run
/// `ailib login` again").
/// </para>
///
/// <para>
/// Cache initialization is lazy (first request) so an MCP host that
/// never makes an authenticated call doesn't pay the file-IO cost.
/// MSAL's own in-memory cache absorbs the steady-state hot path; we
/// only round-trip to the persistent cache on cold starts and to
/// Entra on actual refresh.
/// </para>
/// </summary>
public sealed class MsalSilentBearerTokenProvider : IBearerTokenProvider, IDisposable, IAsyncDisposable
{
	private readonly IOptions<McpAuthOptions> _options;
	private readonly ILogger<MsalSilentBearerTokenProvider> _logger;

	private readonly SemaphoreSlim _initLock = new(1, 1);
	private IPublicClientApplication? _app;
	private MsalCacheHelper? _cacheHelper;
	private bool _disposed;

	/// <summary>Creates the provider; MSAL state is built lazily on first use.</summary>
	public MsalSilentBearerTokenProvider(
		IOptions<McpAuthOptions> options,
		ILogger<MsalSilentBearerTokenProvider> logger)
	{
		_options = options;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
	{
		var opts = _options.Value;
		if (!opts.IsMsalConfigured)
		{
			return null;
		}

		var app = await GetOrCreateAppAsync(opts).ConfigureAwait(false);
		var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
		var account = accounts.FirstOrDefault();
		if (account is null)
		{
			_logger.LogDebug("MSAL silent acquisition skipped — no cached account.");
			return null;
		}

		try
		{
			var result = await app.AcquireTokenSilent(opts.ApiScopes, account)
				.ExecuteAsync(cancellationToken)
				.ConfigureAwait(false);
			return result.AccessToken;
		}
		catch (MsalUiRequiredException ex)
		{
			// Refresh token expired or revoked, conditional-access challenge,
			// account removed — every recoverable cause requires a human at
			// the device-code prompt. Surface as a structured warning rather
			// than throwing through the HTTP path.
			_logger.LogWarning(
				"MSAL silent acquisition requires UI: {Reason}. Run `ailib login` to re-authenticate.",
				ex.Message);
			return null;
		}
		catch (MsalServiceException ex)
		{
			_logger.LogWarning(ex, "MSAL service error during silent acquisition.");
			return null;
		}
	}

	private async Task<IPublicClientApplication> GetOrCreateAppAsync(McpAuthOptions opts)
	{
		if (_app is not null)
		{
			return _app;
		}

		await _initLock.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_app is not null)
			{
				return _app;
			}

			var app = PublicClientApplicationBuilder
				.Create(opts.ClientId)
				.WithTenantId(opts.TenantId)
				.Build();

			var cacheDir = string.IsNullOrWhiteSpace(opts.CacheDirectory)
				? DefaultCacheDirectory()
				: opts.CacheDirectory;

			Directory.CreateDirectory(cacheDir);

			var storage = new StorageCreationPropertiesBuilder(opts.CacheFileName, cacheDir).Build();
			var helper = await MsalCacheHelper.CreateAsync(storage, logger: null).ConfigureAwait(false);
			helper.RegisterCache(app.UserTokenCache);

			_cacheHelper = helper;
			_app = app;

			_logger.LogInformation(
				"MSAL silent token provider ready (clientId={ClientId}, scopes={ScopeCount}, cacheDir={CacheDir}).",
				opts.ClientId,
				opts.ApiScopes.Length,
				cacheDir);

			return _app;
		}
		finally
		{
			_initLock.Release();
		}
	}

	private static string DefaultCacheDirectory()
		=> Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"AiLibrarian",
			"Cli");

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_initLock.Dispose();
		_ = _cacheHelper; // MsalCacheHelper holds OS handles; no explicit dispose API in the public surface.
	}
}
