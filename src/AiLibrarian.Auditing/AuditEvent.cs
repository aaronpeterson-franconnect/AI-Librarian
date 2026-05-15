namespace AiLibrarian.Auditing;

/// <summary>
/// A single SOC2-grade audit event — append-only per ADR 0010.
/// Maps directly to the <c>audit_events</c> Postgres table.
/// </summary>
/// <param name="Id">Stable identifier; assigned by the writer.</param>
/// <param name="OccurredAt">When the action happened.</param>
/// <param name="ActorUserId">The user who took the action; <see cref="AuditConstants.SystemUserId"/>
/// for agent actions.</param>
/// <param name="ActorRole">The effective role at action time, if applicable.</param>
/// <param name="OriginatedBy">For agent chains, the human who triggered them.</param>
/// <param name="DepartmentId">Department context, if applicable.</param>
/// <param name="EventType">The major event family (e.g. <c>source</c>,
/// <c>wiki</c>, <c>persona_action</c>) — see ADR 0010.</param>
/// <param name="EventSubtype">Sub-classifier (e.g. <c>committed</c>,
/// <c>shadowed</c>).</param>
/// <param name="TargetKind">Kind of target (e.g. <c>source</c>,
/// <c>wiki_page</c>, <c>persona_action_record</c>).</param>
/// <param name="TargetId">Identifier of the target row, if any.</param>
/// <param name="CorrelationId">Ties multi-step flows together.</param>
/// <param name="Outcome">Whether the action succeeded, failed, or partially succeeded.</param>
/// <param name="ErrorClass">Optional error categorization on failure.</param>
/// <param name="Llm">Optional per-call LLM telemetry; metadata-only per ADR 0010.</param>
/// <param name="Details">Additional structured detail, JSON-serialized into
/// the <c>details</c> JSONB column.</param>
public sealed record AuditEvent(
	Guid Id,
	DateTimeOffset OccurredAt,
	Guid ActorUserId,
	string? ActorRole,
	Guid? OriginatedBy,
	Guid? DepartmentId,
	string EventType,
	string? EventSubtype,
	string? TargetKind,
	Guid? TargetId,
	Guid CorrelationId,
	EventOutcome Outcome,
	string? ErrorClass,
	LlmTelemetry? Llm,
	IReadOnlyDictionary<string, object?> Details);

/// <summary>
/// Outcome classification for an audit event per ADR 0010.
/// </summary>
public enum EventOutcome
{
	/// <summary>The action completed as intended.</summary>
	Success = 0,

	/// <summary>The action failed.</summary>
	Failure = 1,

	/// <summary>The action partially succeeded (some sub-steps failed).</summary>
	Partial = 2,
}

/// <summary>
/// Per-call LLM telemetry — metadata-only per the content-capture
/// policy in ADR 0010. Prompt and completion text are never stored.
/// </summary>
/// <param name="Provider">Provider identifier (e.g. <c>azure-openai</c>).</param>
/// <param name="Model">Model identifier (e.g. <c>gpt-4o</c>).</param>
/// <param name="PromptTokens">Token count fed to the model.</param>
/// <param name="CompletionTokens">Token count generated.</param>
/// <param name="CostEstimateUsd">Cost estimate; provider-specific calculation.</param>
/// <param name="LatencyMs">Wall-clock latency.</param>
/// <param name="PersonaId">Persona under which the call ran, per ADR 0007 amendment.</param>
public sealed record LlmTelemetry(
	string Provider,
	string Model,
	int PromptTokens,
	int CompletionTokens,
	decimal? CostEstimateUsd,
	int LatencyMs,
	Guid? PersonaId);

/// <summary>
/// Well-known constants shared by audit writers.
/// </summary>
public static class AuditConstants
{
	/// <summary>
	/// Sentinel <c>actor_user_id</c> used for autonomous agent actions
	/// where there is no human originator. Matches the seed row in
	/// <c>users</c>.
	/// </summary>
	public static readonly Guid SystemUserId = new("00000000-0000-0000-0000-00000000FFFF");
}
