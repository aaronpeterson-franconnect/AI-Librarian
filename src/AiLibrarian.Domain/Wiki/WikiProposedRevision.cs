using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// One row in <c>wiki_proposed_revisions</c>. Phase 2.5 approval queue
/// per ADR 0006 Q13. When a <c>wiki_pages.locked</c> page would
/// receive a new revision, the Wiki Maintainer instead lands the
/// proposed content here and waits for a Reviewer/Librarian decision.
/// </summary>
/// <param name="Id">Stable proposal id.</param>
/// <param name="PageId">Parent wiki page.</param>
/// <param name="MinClassification">Facet classification ceiling the proposal targets.</param>
/// <param name="PersonaId">Persona facet variant (null = persona-neutral).</param>
/// <param name="ProposedRevisionNumber">The revno this proposal would have been if directly committed.</param>
/// <param name="AuthoredBy">Author <see cref="Users.UserRow.Id"/>; system sentinel for autonomous writes.</param>
/// <param name="AuthoredAt">Proposal-create timestamp.</param>
/// <param name="ExpiresAt">Auto-rejection deadline. ADR 0006 Q13 default = +14 days.</param>
/// <param name="BodyMarkdown">Assembled body at proposal time (snapshot, with citation tokens stripped).</param>
/// <param name="Payload">Claims + citations the proposal would commit on accept. See <see cref="WikiProposalPayload"/>.</param>
/// <param name="State">Lifecycle state: <c>pending</c>, <c>accepted</c>, <c>rejected</c>, or <c>expired</c>.</param>
/// <param name="DecidedBy">User who accepted/rejected; null when <see cref="State"/> is <c>pending</c>.</param>
/// <param name="DecidedAt">When the decision was made; null when <see cref="State"/> is <c>pending</c>.</param>
/// <param name="DecisionReason">Operator-supplied note for rejected/expired proposals; null otherwise.</param>
public sealed record WikiProposedRevision(
	Guid Id,
	Guid PageId,
	Classification MinClassification,
	Guid? PersonaId,
	int ProposedRevisionNumber,
	Guid AuthoredBy,
	DateTimeOffset AuthoredAt,
	DateTimeOffset ExpiresAt,
	string BodyMarkdown,
	WikiProposalPayload Payload,
	ProposalState State,
	Guid? DecidedBy,
	DateTimeOffset? DecidedAt,
	string? DecisionReason);

/// <summary>The four states a proposal can be in.</summary>
public enum ProposalState
{
	/// <summary>Awaiting a Reviewer/Librarian decision.</summary>
	Pending = 0,

	/// <summary>Accepted; the API has materialized the proposal into a real revision.</summary>
	Accepted = 1,

	/// <summary>Rejected by a Reviewer/Librarian with a reason.</summary>
	Rejected = 2,

	/// <summary>Auto-rejected because <see cref="WikiProposedRevision.ExpiresAt"/> elapsed without a decision.</summary>
	Expired = 3,
}

/// <summary>
/// The JSONB payload shape persisted alongside a proposal. Holds the
/// claims + citations the maintainer would have committed. On accept,
/// the API copies this into a fresh <c>wiki_page_revisions</c> +
/// <c>wiki_claims</c> + <c>wiki_claim_citations</c> transaction
/// (keeping <c>wiki_claims</c> immutable per ADR 0007).
/// </summary>
/// <param name="Claims">The proposed claims, in stable order.</param>
public sealed record WikiProposalPayload(IReadOnlyList<WikiClaimDraft> Claims);

/// <summary>Helpers for converting <see cref="ProposalState"/> to/from the schema's text representation.</summary>
public static class ProposalStateCodes
{
	/// <summary>Stable text form matching the schema's <c>chk_wiki_proposed_revisions_state</c>.</summary>
	public static string ToCode(this ProposalState state) => state switch
	{
		ProposalState.Pending => "pending",
		ProposalState.Accepted => "accepted",
		ProposalState.Rejected => "rejected",
		ProposalState.Expired => "expired",
		_ => "pending",
	};

	/// <summary>Parse the schema's text form. Unknown values fall back to <see cref="ProposalState.Pending"/>.</summary>
	public static ProposalState Parse(string raw) => raw switch
	{
		"pending" => ProposalState.Pending,
		"accepted" => ProposalState.Accepted,
		"rejected" => ProposalState.Rejected,
		"expired" => ProposalState.Expired,
		_ => ProposalState.Pending,
	};
}
