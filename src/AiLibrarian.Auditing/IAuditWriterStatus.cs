namespace AiLibrarian.Auditing;

/// <summary>
/// Operator-facing status of the audit writer. Surfaced through
/// <c>/health</c> and operator dashboards so a green health response
/// reflects audit availability — without it, an audit outage in
/// degraded mode is invisible to readiness probes.
/// </summary>
public interface IAuditWriterStatus
{
	/// <summary>Snapshot the current writer state.</summary>
	AuditWriterStatusSnapshot GetStatus();
}

/// <summary>Snapshot of audit-writer state for operator visibility.</summary>
/// <param name="Mode">Configured writer mode (e.g. <c>Postgres</c> or <c>NoOp</c>).</param>
/// <param name="DegradedModeAllowed">Whether the writer is allowed to drop events on failure.</param>
/// <param name="CircuitState">Current circuit-breaker state name (e.g. <c>Closed</c>, <c>Open</c>).</param>
public sealed record AuditWriterStatusSnapshot(
	string Mode,
	bool DegradedModeAllowed,
	string CircuitState);
