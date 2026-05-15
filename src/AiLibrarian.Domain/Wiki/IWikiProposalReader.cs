namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// Read side of <c>wiki_proposed_revisions</c>. Runs in the caller's
/// RLS context — a Reviewer only sees proposals on pages in their
/// reviewer departments; an Admin sees everything. Used by the admin
/// list endpoint and as a precondition check inside
/// <see cref="IWikiProposalWriter.DecideAsync"/>.
/// </summary>
public interface IWikiProposalReader
{
	/// <summary>Fetch a single proposal by id, or null when missing / RLS-hidden.</summary>
	Task<WikiProposedRevision?> GetAsync(
		Guid proposalId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// List proposals visible to the caller, most-recent-first. Filter
	/// by state when non-null; null means "all states." Limit caps the
	/// result; default is 50.
	/// </summary>
	Task<IReadOnlyList<WikiProposedRevision>> ListAsync(
		ProposalState? state,
		int limit,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// List proposals filtered by who decided them and / or when. Used
	/// by the per-librarian "my recent decisions" audit endpoint. The
	/// result includes only proposals whose <c>state</c> is
	/// <see cref="ProposalState.Accepted"/>, <see cref="ProposalState.Rejected"/>,
	/// or <see cref="ProposalState.Expired"/> — pending proposals never
	/// have a decider so they're never relevant here.
	/// </summary>
	/// <param name="decidedBy">When non-null, restrict to decisions made by this user.</param>
	/// <param name="since">When non-null, restrict to decisions on or after this instant.</param>
	/// <param name="limit">Max rows returned, ordered by <c>decided_at</c> descending.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<IReadOnlyList<WikiProposedRevision>> ListDecidedAsync(
		Guid? decidedBy,
		DateTimeOffset? since,
		int limit,
		CancellationToken cancellationToken = default);
}
