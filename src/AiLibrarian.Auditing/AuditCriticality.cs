namespace AiLibrarian.Auditing;

/// <summary>
/// Per-call audit criticality, per ADR 0010's split between
/// "best-effort audit on read-side queries" and "fail-closed audit on
/// write actions and security-relevant events." The
/// <see cref="IAuditWriter"/> contract honors this distinction; the
/// circuit breaker uses it to decide whether to throw on inner-writer
/// failure or log-and-drop.
/// </summary>
public enum AuditCriticality
{
	/// <summary>
	/// Caller must not be blocked by audit failures. The writer logs
	/// any inner failure and returns; it does not throw, does not roll
	/// back the caller's transaction, and does not trip the circuit
	/// breaker. Use for: search/list/get-page reads, LLM telemetry on
	/// already-delivered streaming responses, MCP query-tool audit.
	/// </summary>
	BestEffort = 0,

	/// <summary>
	/// Caller's action must not commit without an audit row. Inner
	/// failures throw <c>AuditWriterUnavailableException</c> so the
	/// calling transaction rolls back; sustained failure trips the
	/// circuit breaker; degraded-mode drops are themselves audited.
	/// Use for: source upload, ingest enqueue, source approval,
	/// page-lock toggle, classification change, persona-action commit,
	/// startup probes.
	/// </summary>
	Critical = 1,
}
