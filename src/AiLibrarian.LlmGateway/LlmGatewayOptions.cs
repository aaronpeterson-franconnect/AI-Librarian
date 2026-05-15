using AiLibrarian.LlmGateway.Abstractions;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// Configuration root for the LLM gateway, bound from the
/// <c>LlmGateway:</c> section of <c>appsettings</c>. Per ADR 0012,
/// every configured provider must declare a <see cref="ProviderTier"/>
/// and a <see cref="DataHandlingProfile"/>; missing or
/// <see cref="ProviderTier.Unverified"/> tiers emit a startup-audit
/// warning so Security/Ops can act.
/// </summary>
public sealed class LlmGatewayOptions
{
	/// <summary>Configuration section name in <c>appsettings.json</c>.</summary>
	public const string SectionName = "LlmGateway";

	/// <summary>Identifier of the default provider for chat completions.</summary>
	public string DefaultChatProvider { get; init; } = string.Empty;

	/// <summary>Identifier of the default provider for embeddings.</summary>
	public string DefaultEmbeddingProvider { get; init; } = string.Empty;

	/// <summary>Identifier of the default provider for reranking.</summary>
	public string? DefaultRerankProvider { get; init; }

	/// <summary>Configured providers, keyed by provider id.</summary>
	public IReadOnlyDictionary<string, ProviderOptions> Providers { get; init; }
		= new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-provider configuration including tier metadata per ADR 0012.</summary>
public sealed class ProviderOptions
{
	/// <summary>Human-readable provider name.</summary>
	public string DisplayName { get; init; } = string.Empty;

	/// <summary>Whether this provider is active in the current configuration.</summary>
	public bool Enabled { get; init; }

	/// <summary>Provider tier. Defaults to <see cref="ProviderTier.Unverified"/>
	/// to ensure missing configuration emits a warning rather than a silent pass.</summary>
	public ProviderTier Tier { get; init; } = ProviderTier.Unverified;

	/// <summary>Documented data-handling commitments. Required for non-Unverified tiers.</summary>
	public DataHandlingProfileOptions? DataHandling { get; init; }

	/// <summary>Endpoint URI (e.g. Azure OpenAI resource URL).</summary>
	public string? Endpoint { get; init; }

	/// <summary>Azure OpenAI <b>deployment name</b> for chat completions. Distinct
	/// from the public model name; required when <see cref="Enabled"/> is true.</summary>
	public string? ChatDeployment { get; init; }

	/// <summary>Azure OpenAI <b>deployment name</b> for text embeddings.</summary>
	public string? EmbeddingDeployment { get; init; }

	/// <summary>Optional API key. When null or empty, the gateway uses
	/// <c>DefaultAzureCredential</c> (managed identity, Azure CLI, etc.).</summary>
	public string? ApiKey { get; init; }

	/// <summary>Models exposed by this provider (informational / UI; deployments drive calls).</summary>
	public IReadOnlyCollection<string> Models { get; init; } = Array.Empty<string>();
}

/// <summary>Configuration shape for <see cref="DataHandlingProfile"/>.</summary>
public sealed class DataHandlingProfileOptions
{
	/// <summary>Provider commits to not training models on our data.</summary>
	public bool NoTraining { get; init; }

	/// <summary>Maximum retention in days; zero means no retention beyond the request.</summary>
	public int RetentionDays { get; init; }

	/// <summary>Provider commits to no human review without explicit consent.</summary>
	public bool NoHumanReview { get; init; }

	/// <summary>Content stays inside our tenant boundary.</summary>
	public bool TenantBound { get; init; }

	/// <summary>Reference to the relevant agreement.</summary>
	public string ContractReference { get; init; } = string.Empty;

	/// <summary>Project to the immutable domain shape used by audit + diagnostics.</summary>
	public DataHandlingProfile ToProfile() => new(
		NoTraining: NoTraining,
		RetentionDays: RetentionDays,
		NoHumanReview: NoHumanReview,
		TenantBound: TenantBound,
		ContractReference: ContractReference);
}
