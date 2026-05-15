namespace AiLibrarian.WikiMaintainer;

/// <summary>
/// The Wiki Maintainer contract per ADR 0006. Given a page + facet +
/// source chunk pool, produce a new revision (or reject it cleanly).
/// Single-call shape — multi-facet pages run one call per facet so the
/// caller can manage failure isolation.
///
/// <para>This is the contract the future Cascade-Regeneration Worker
/// (Phase 4) and the operator-triggered <c>POST /api/admin/wiki/regenerate</c>
/// endpoint (Phase 2.5) both call.</para>
/// </summary>
public interface IWikiMaintainer
{
	/// <summary>Run one Pass-1-then-Pass-2 pipeline. Idempotent on the (page, facet, revno) tuple.</summary>
	Task<WikiMaintenanceResult> GenerateRevisionAsync(
		WikiMaintenanceRequest request,
		CancellationToken cancellationToken = default);
}
