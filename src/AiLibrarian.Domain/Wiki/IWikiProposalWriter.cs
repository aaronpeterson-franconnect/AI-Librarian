namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// Write side of <c>wiki_proposed_revisions</c>. Three operations:
/// <list type="bullet">
///   <item><see cref="CreateAsync"/> — emit a new pending proposal
///         (called by the Wiki Maintainer when the target page is
///         locked).</item>
///   <item><see cref="DecideAsync"/> — transition pending → accepted
///         or pending → rejected (called by Reviewer/Librarian via
///         the admin endpoints).</item>
///   <item><see cref="ExpirePendingAsync"/> — sweep stale pending
///         proposals into <see cref="ProposalState.Expired"/>
///         (called by the periodic worker).</item>
/// </list>
///
/// <para>The accept path's "materialize into a real revision" step is
/// the writer's responsibility too — it owns the
/// (wiki_page_revisions + wiki_claims + wiki_claim_citations + facet
/// pointer update) transaction so callers don't have to know the
/// downstream wiring.</para>
/// </summary>
public interface IWikiProposalWriter
{
	/// <summary>Create a new pending proposal. Throws on unique-index violation if a pending proposal already exists for the same facet.</summary>
	Task<Guid> CreateAsync(
		WikiProposedRevision proposal,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Apply a decision to a pending proposal. When
	/// <paramref name="decision"/> is <see cref="ProposalState.Accepted"/>,
	/// the writer also commits the proposal's payload as a real
	/// revision in the same transaction. Throws when the proposal is
	/// not pending or doesn't exist.
	/// </summary>
	/// <returns>The new <c>wiki_page_revisions.id</c> when accepted; null when rejected.</returns>
	Task<Guid?> DecideAsync(
		Guid proposalId,
		ProposalState decision,
		Guid decidedBy,
		string? reason,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Transition every pending proposal whose <c>expires_at</c> has
	/// elapsed into <see cref="ProposalState.Expired"/>. Returns the
	/// number of rows updated. Runs in system admin context.
	/// </summary>
	Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Reject a batch of pending proposals in one transaction. Useful
	/// after a source-retirement sweep: rather than calling
	/// <see cref="DecideAsync"/> N times, the operator submits a list of
	/// ids and a single reason. Proposals not in
	/// <see cref="ProposalState.Pending"/> are skipped (the per-id
	/// outcome distinguishes "rejected" from "skipped" so the caller can
	/// surface a partial-success summary). Runs in system admin context.
	/// </summary>
	/// <param name="proposalIds">Distinct proposal ids to reject.</param>
	/// <param name="decidedBy">The deciding user; stamped on every transitioned row.</param>
	/// <param name="reason">Operator-supplied note; same reason applied to every row.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<BulkRejectOutcome> BulkRejectAsync(
		IReadOnlyCollection<Guid> proposalIds,
		Guid decidedBy,
		string reason,
		CancellationToken cancellationToken = default);
}

/// <summary>Outcome of an <see cref="IWikiProposalWriter.BulkRejectAsync"/> call.</summary>
/// <param name="Rejected">Ids that were pending and were transitioned to <c>rejected</c>.</param>
/// <param name="Skipped">Ids that existed but were not pending (already accepted/rejected/expired).</param>
/// <param name="NotFound">Ids that didn't exist in <c>wiki_proposed_revisions</c>.</param>
public sealed record BulkRejectOutcome(
	IReadOnlyList<Guid> Rejected,
	IReadOnlyList<Guid> Skipped,
	IReadOnlyList<Guid> NotFound);
