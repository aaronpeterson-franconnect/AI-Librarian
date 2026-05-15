using Microsoft.Extensions.Options;

namespace AiLibrarian.Portal.Services;

/// <summary>
/// Dev-mode <see cref="IPortalUserContext"/>. Reads the configured
/// roster from <see cref="PortalOptions.DevContributors"/> and lets
/// the Upload page swap between them. Used when
/// <c>AzureAd:ClientId</c> is empty (the pilot's Phase 1 path).
///
/// State is per-circuit (scoped DI) so picking a contributor in one
/// browser tab doesn't bleed into another operator's session.
/// </summary>
internal sealed class DevPortalUserContext : IPortalUserContext
{
	private readonly PortalOptions _options;
	private string _selectedContributorId;

	public DevPortalUserContext(IOptions<PortalOptions> options)
	{
		_options = options.Value;

		// Default the selection: prefer the configured DefaultContributorId,
		// else first DevContributor in the roster, else empty (the upload
		// form will require the operator to pick / paste).
		if (!string.IsNullOrWhiteSpace(_options.DefaultContributorId))
		{
			_selectedContributorId = _options.DefaultContributorId.Trim();
		}
		else if (_options.DevContributors.Count > 0)
		{
			_selectedContributorId = _options.DevContributors[0].Id;
		}
		else
		{
			_selectedContributorId = string.Empty;
		}
	}

	public bool EntraEnabled => false;

	// In dev mode "authenticated" tracks "has a contributor been chosen."
	// The upload page disables the Submit button on the empty case.
	public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_selectedContributorId);

	public string DisplayName
	{
		get
		{
			var match = _options.DevContributors
				.FirstOrDefault(c => string.Equals(c.Id, _selectedContributorId, StringComparison.OrdinalIgnoreCase));
			return match?.DisplayName ?? string.Empty;
		}
	}

	public string ContributorId => _selectedContributorId;

	public IReadOnlyList<DevContributor> SelectableContributors => _options.DevContributors;

	public void SelectContributor(string contributorId)
	{
		_selectedContributorId = contributorId?.Trim() ?? string.Empty;
	}
}
