namespace AiLibrarian.Infrastructure.Auditing;

/// <summary>
/// Configuration for the audit-ledger writer. Bound from the
/// <c>Auditing</c> section of the host's configuration.
/// </summary>
public sealed class AuditingOptions
{
	/// <summary>Configuration section name.</summary>
	public const string SectionName = "Auditing";

	/// <summary>
	/// Selects the <c>IAuditWriter</c> implementation to register.
	/// Defaults to <see cref="AuditWriterMode.Postgres"/>; the API
	/// composition root falls back to <see cref="AuditWriterMode.NoOp"/>
	/// only when <c>ConnectionStrings:Postgres</c> is missing
	/// (Phase 0 dev-without-Postgres mode).
	/// </summary>
	public AuditWriterMode WriterMode { get; set; } = AuditWriterMode.Postgres;

	/// <summary>
	/// When <see langword="true"/>, the audit writer is allowed to drop
	/// events (with a warning log + Sentinel-routable signal) once the
	/// circuit breaker is open. When <see langword="false"/> (the
	/// production default), audit failures fail closed — the calling
	/// action is rolled back. Per ADR 0010 this flag must default off
	/// in any environment writing real customer data; flipping it on is
	/// itself an audited transition and must be approved by the
	/// platform operator.
	/// </summary>
	public bool DegradedModeAllowed { get; set; }

	/// <summary>Circuit-breaker thresholds applied to the Postgres writer.</summary>
	public CircuitBreakerOptions CircuitBreaker { get; set; } = new();

	/// <summary>
	/// When <see langword="true"/> (production default), the API
	/// refuses to start if the audit writer's startup probe cannot
	/// reach the ledger and degraded mode is not allowed. Set to
	/// <see langword="false"/> for unit-test hosts that don't need a
	/// real Postgres.
	/// </summary>
	public bool RefuseToStartOnProbeFailure { get; set; } = true;
}

/// <summary>Selects which <c>IAuditWriter</c> the DI extension registers.</summary>
public enum AuditWriterMode
{
	/// <summary>Postgres-backed writer with circuit breaker (production default).</summary>
	Postgres = 0,

	/// <summary>NoOp logger-only writer; unit tests and bootstrap only.</summary>
	NoOp = 1,
}

/// <summary>Circuit-breaker tuning for <see cref="AuditWriterCircuitBreaker"/>.</summary>
public sealed class CircuitBreakerOptions
{
	/// <summary>
	/// Consecutive write failures before the breaker opens. Defaults
	/// to 5; lower values trade resilience for faster fail-closed.
	/// </summary>
	public int FailureThreshold { get; set; } = 5;

	/// <summary>How long the breaker stays open before probing in HalfOpen.</summary>
	public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
