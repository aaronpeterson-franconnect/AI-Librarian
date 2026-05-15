namespace AiLibrarian.Auditing;

/// <summary>
/// The single audit-writer contract — every component that records
/// an action does so through this interface. Per ADR 0010, audit
/// behavior splits by call-site intent: read-side queries pass
/// <see cref="AuditCriticality.BestEffort"/> so audit hiccups don't
/// break user requests; write-side actions pass
/// <see cref="AuditCriticality.Critical"/> so the calling transaction
/// rolls back if the ledger is unreachable.
/// </summary>
public interface IAuditWriter
{
	/// <summary>
	/// Append the given event to the audit ledger.
	/// </summary>
	/// <param name="evt">The event to append.</param>
	/// <param name="criticality">Whether this audit row is required
	/// for the calling action to be considered complete. Defaults to
	/// <see cref="AuditCriticality.Critical"/> — opt into
	/// <see cref="AuditCriticality.BestEffort"/> explicitly for
	/// read-side audit.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A task that completes when the event is durably persisted
	/// (Critical) or when the writer has finished best-effort delivery
	/// (BestEffort, which never throws on inner-writer failure).</returns>
	Task WriteAsync(
		AuditEvent evt,
		AuditCriticality criticality = AuditCriticality.Critical,
		CancellationToken cancellationToken = default);
}
