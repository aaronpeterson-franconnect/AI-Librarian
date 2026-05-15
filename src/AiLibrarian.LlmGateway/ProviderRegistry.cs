using AiLibrarian.LlmGateway.Abstractions;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// Builds the immutable <see cref="ProviderDescriptor"/> set that the
/// gateway publishes at startup — emitted into the
/// <c>audit.startup.providers_configured</c> event per ADR 0010 +
/// ADR 0012. Phase 0 ships only the descriptor build; per-provider
/// chat / embedding / rerank wiring lands later in Phase 0.
/// </summary>
public static class ProviderRegistry
{
	/// <summary>
	/// Project the configured providers into descriptors.
	/// </summary>
	public static IReadOnlyList<ProviderDescriptor> Build(LlmGatewayOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		var descriptors = new List<ProviderDescriptor>(options.Providers.Count);
		foreach (var (id, provider) in options.Providers)
		{
			descriptors.Add(new ProviderDescriptor(
				Id: id,
				DisplayName: provider.DisplayName,
				Tier: provider.Tier,
				DataHandling: provider.DataHandling?.ToProfile(),
				Models: provider.Models,
				Enabled: provider.Enabled));
		}

		return descriptors;
	}
}
