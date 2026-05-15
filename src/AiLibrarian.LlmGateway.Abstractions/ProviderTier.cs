namespace AiLibrarian.LlmGateway.Abstractions;

/// <summary>
/// LLM provider tier classification per ADR 0012. Every configured
/// provider must declare a tier; <see cref="Unverified"/> emits a
/// startup-audit warning so Security/Ops can act.
/// </summary>
public enum ProviderTier
{
	/// <summary>
	/// Provider declares no tier or the configured tier could not be
	/// confirmed. Treated as a warning at startup, not a hard fail —
	/// per the "warn and proceed" enforcement choice on ADR 0012.
	/// </summary>
	Unverified = 0,

	/// <summary>
	/// Provider is on a documented enterprise-tier agreement with
	/// no-training, bounded-retention, no-human-review-without-consent,
	/// and tenant-bound data-handling commitments. The required default
	/// for any production deployment.
	/// </summary>
	Enterprise = 1,

	/// <summary>
	/// Provider is a self-hosted runtime (Ollama, vLLM) operated inside
	/// our tenant. Inherits the same data-handling guarantees by
	/// construction; tracked separately for clarity.
	/// </summary>
	SelfHosted = 2,
}

/// <summary>
/// Documented data-handling commitments for an LLM provider — per ADR 0012.
/// Captured at configuration time; emitted into the startup audit event.
/// </summary>
/// <param name="NoTraining">Provider commits to not training models on our data.</param>
/// <param name="RetentionDays">Maximum retention of in-flight content
/// before deletion, in days. Zero means no retention beyond the request.</param>
/// <param name="NoHumanReview">Provider commits to no human review of
/// content without explicit consent.</param>
/// <param name="TenantBound">Content is processed and stored only inside
/// our tenant boundary.</param>
/// <param name="ContractReference">Reference to the relevant agreement
/// (e.g., "Azure OpenAI Service - Enterprise Agreement, Section 4.2").</param>
public sealed record DataHandlingProfile(
	bool NoTraining,
	int RetentionDays,
	bool NoHumanReview,
	bool TenantBound,
	string ContractReference);
