namespace AiLibrarian.Auditing;

/// <summary>
/// Read-side companion to <see cref="IAuditWriter"/>. The librarian
/// dashboard (Phase 2) and SIEM-export adapters consume this interface;
/// the implementation lives in <c>AiLibrarian.Infrastructure</c> and
/// applies the RLS read predicate from changeset <c>0099-rls-audit-1</c>
/// (admin sees everything, librarians see their own departments).
///
/// <para>
/// Pre-Phase-1 the interface is intentionally narrow — only the queries
/// the startup probe and integration tests need. The Phase 2 dashboard
/// will widen the surface (filters by event_type, time range, actor,
/// correlation, etc.) once the wire-up exists.
/// </para>
/// </summary>
public interface IAuditQueryService
{
	/// <summary>
	/// Health probe — returns <see langword="true"/> when the audit
	/// ledger is reachable and the configured connection has the
	/// permissions needed to read <c>audit_events</c>.
	/// </summary>
	Task<bool> IsLedgerReachableAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Returns the most recent <paramref name="take"/> events visible to
	/// the calling RLS context. Used for spot-check verification by the
	/// startup probe and tests; the librarian dashboard will use a
	/// richer query API once Phase 2 lands.
	/// </summary>
	Task<IReadOnlyList<AuditEvent>> RecentAsync(
		int take,
		CancellationToken cancellationToken = default);
}
