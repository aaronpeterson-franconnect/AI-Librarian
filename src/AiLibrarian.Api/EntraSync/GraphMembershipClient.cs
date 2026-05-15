using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace AiLibrarian.Api.EntraSync;

/// <summary>
/// Real <see cref="IGraphMembershipClient"/> backed by the v1 Graph
/// REST endpoint. Deliberately does NOT pull in <c>Microsoft.Graph</c>
/// — that SDK adds 10MB+ of transitive dependencies for what is a
/// single endpoint call. Uses <c>Microsoft.Identity.Client</c>
/// (already in the dependency graph for MCP auth) for the
/// client-credentials flow token acquisition.
///
/// <para>The token is cached in-process for its lifetime; MSAL handles
/// the refresh-before-expiry math.</para>
/// </summary>
public sealed class GraphMembershipClient : IGraphMembershipClient, IDisposable
{
	private static readonly Uri GraphBaseUri = new("https://graph.microsoft.com/v1.0/");
	private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

	private readonly HttpClient _http;
	private readonly IConfidentialClientApplication _msal;
	private readonly ILogger<GraphMembershipClient> _logger;
	private readonly bool _ownsHttpClient;
	private readonly SemaphoreSlim _msalLock = new(1, 1);
	private bool _disposed;

	/// <summary>
	/// Creates the client. Throws when required options are missing —
	/// the sync hosted service catches this and reports the misconfiguration
	/// as a startup-warning rather than crashing the host.
	/// </summary>
	public GraphMembershipClient(
		IOptions<EntraGroupSyncOptions> options,
		ILogger<GraphMembershipClient> logger,
		HttpClient? httpClient = null)
	{
		var opts = options.Value;
		if (string.IsNullOrWhiteSpace(opts.TenantId) || string.IsNullOrWhiteSpace(opts.ClientId) || string.IsNullOrWhiteSpace(opts.ClientSecret))
		{
			throw new InvalidOperationException(
				"EntraSync requires TenantId, ClientId, and ClientSecret. Set them or disable EntraSync:Enabled.");
		}

		_logger = logger;
		_http = httpClient ?? new HttpClient { BaseAddress = GraphBaseUri, Timeout = opts.GraphRequestTimeout };
		_ownsHttpClient = httpClient is null;
		if (_http.BaseAddress is null)
		{
			_http.BaseAddress = GraphBaseUri;
		}

		_msal = ConfidentialClientApplicationBuilder
			.Create(opts.ClientId)
			.WithClientSecret(opts.ClientSecret)
			.WithTenantId(opts.TenantId)
			.Build();
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<Guid>> ListGroupMemberOidsAsync(
		string groupObjectId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(groupObjectId);
		ObjectDisposedException.ThrowIf(_disposed, this);

		var token = await AcquireTokenAsync(cancellationToken).ConfigureAwait(false);

		var results = new List<Guid>();
		// Filter to users only ($select=id keeps the response small; the
		// /members endpoint can return service principals + nested groups
		// otherwise -- $filter to microsoft.graph.user trims those out).
		var url = $"groups/{Uri.EscapeDataString(groupObjectId)}/members/microsoft.graph.user?$select=id&$top=999";

		while (!string.IsNullOrEmpty(url))
		{
			cancellationToken.ThrowIfCancellationRequested();

			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

			using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
			{
				throw new InvalidOperationException(
					$"Graph /groups/{groupObjectId}/members returned {(int)resp.StatusCode}: "
					+ (body.Length > 500 ? body[..500] : body));
			}

			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in arr.EnumerateArray())
				{
					if (item.TryGetProperty("id", out var idEl) && idEl.GetString() is { } idStr
						&& Guid.TryParse(idStr, out var oid))
					{
						results.Add(oid);
					}
				}
			}

			url = doc.RootElement.TryGetProperty("@odata.nextLink", out var nextEl) && nextEl.GetString() is { } next
				? next
				: string.Empty;

			// If next is an absolute URL, switch the request to absolute; HttpClient
			// resolves it against BaseAddress otherwise (which is fine for the first
			// page but wrong if the next-link is fully qualified).
			if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
			{
				// Use absolute URL verbatim by clearing BaseAddress influence;
				// the next iteration's HttpRequestMessage will use the absolute URI.
			}
		}

		_logger.LogDebug("Graph group {Group} has {Count} member(s).", groupObjectId, results.Count);
		return results;
	}

	private async Task<string> AcquireTokenAsync(CancellationToken cancellationToken)
	{
		await _msalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var result = await _msal
				.AcquireTokenForClient(GraphScopes)
				.ExecuteAsync(cancellationToken)
				.ConfigureAwait(false);
			return result.AccessToken;
		}
		finally
		{
			_msalLock.Release();
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_msalLock.Dispose();
		if (_ownsHttpClient)
		{
			_http.Dispose();
		}
	}
}
