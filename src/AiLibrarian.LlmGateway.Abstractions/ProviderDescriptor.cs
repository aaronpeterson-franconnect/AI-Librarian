namespace AiLibrarian.LlmGateway.Abstractions;

/// <summary>
/// Static description of a configured LLM provider — what it is, what
/// it offers, and how its data-handling is documented (per ADR 0012).
/// Materialized from configuration at startup; emitted into the
/// <c>audit.startup.providers_configured</c> event.
/// </summary>
/// <param name="Id">Provider identifier (e.g. <c>azure-openai</c>).</param>
/// <param name="DisplayName">Human-readable provider name.</param>
/// <param name="Tier">Data-handling tier per ADR 0012.</param>
/// <param name="DataHandling">Documented data-handling commitments;
/// null when <see cref="Tier"/> is <see cref="ProviderTier.Unverified"/>.</param>
/// <param name="Models">The model identifiers this provider exposes.</param>
/// <param name="Enabled">Whether the provider is active in the current configuration.</param>
public sealed record ProviderDescriptor(
	string Id,
	string DisplayName,
	ProviderTier Tier,
	DataHandlingProfile? DataHandling,
	IReadOnlyCollection<string> Models,
	bool Enabled);
