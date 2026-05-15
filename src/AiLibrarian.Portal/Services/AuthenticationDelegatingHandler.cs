using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace AiLibrarian.Portal.Services;

/// <summary>
/// Attaches a bearer access token to every outbound request to the API.
/// Only active when Entra sign-in is configured; the DI registration
/// in <c>Program.cs</c> skips this handler when <c>AzureAd:ClientId</c>
/// is empty so the dev-mode fallback keeps working with anonymous
/// calls.
///
/// <para>Uses <c>ITokenAcquisition.GetAccessTokenForUserAsync</c> —
/// Microsoft.Identity.Web handles MSAL caching, refresh, and the
/// on-behalf-of flow against the configured downstream API scope.</para>
/// </summary>
internal sealed class AuthenticationDelegatingHandler : DelegatingHandler
{
	private readonly ITokenAcquisition _tokenAcquisition;
	private readonly PortalDownstreamApiOptions _options;
	private readonly ILogger<AuthenticationDelegatingHandler> _logger;

	public AuthenticationDelegatingHandler(
		ITokenAcquisition tokenAcquisition,
		IOptions<PortalDownstreamApiOptions> options,
		ILogger<AuthenticationDelegatingHandler> logger)
	{
		_tokenAcquisition = tokenAcquisition;
		_options = options.Value;
		_logger = logger;
	}

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		// Already has Authorization (e.g. test code) -> respect it.
		if (request.Headers.Authorization is not null)
		{
			return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
		}

		var scopes = _options.Scopes;
		if (scopes.Count == 0)
		{
			// Misconfigured deployment: Entra enabled but no downstream API
			// scope set. Log loudly and proceed without a token so the API
			// returns 401 cleanly instead of the request hanging.
			_logger.LogWarning(
				"DownstreamApi:Scopes is empty; API call to {Uri} will be sent without a bearer token.",
				request.RequestUri);
			return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
		}

		try
		{
			var token = await _tokenAcquisition
				.GetAccessTokenForUserAsync(scopes)
				.ConfigureAwait(false);

			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}
		catch (MicrosoftIdentityWebChallengeUserException ex)
		{
			// User's session lapsed; rethrow so middleware redirects to sign-in.
			_logger.LogInformation(ex, "Token acquisition required user challenge; rethrowing for sign-in redirect.");
			throw;
		}

		return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>
/// Downstream-API configuration bound from <c>DownstreamApi</c>. Defines
/// the OAuth scope(s) the Portal asks for when calling the AI Librarian
/// API — typically a single value like <c>api://&lt;api-client-id&gt;/access_as_user</c>.
/// </summary>
public sealed class PortalDownstreamApiOptions
{
	/// <summary>Configuration section name.</summary>
	public const string SectionName = "DownstreamApi";

	/// <summary>OAuth scopes for the downstream API. Empty disables token attachment.</summary>
	public List<string> Scopes { get; set; } = new();
}
