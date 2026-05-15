using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// Write-side of the wiki tables for the Wiki Maintainer. Inserts a
/// new <c>wiki_page_revisions</c> row plus its child <c>wiki_claims</c>
/// + <c>wiki_claim_citations</c> rows in one transaction. The writer
/// is the only path that creates revisions; the page row itself is
/// created separately (the Maintainer doesn't decide what pages exist).
///
/// <para>Per ADR 0006 the wiki is LLM-authored only — this writer runs
/// in system admin context so the Admin-only RLS policies on the wiki
/// tables admit it. Application-layer authorization checks (e.g. "did
/// the Citation Validator pass?") happen upstream in
/// <c>WikiMaintainer</c>.</para>
/// </summary>
public interface IWikiRevisionWriter
{
	/// <summary>
	/// Commit <paramref name="draft"/> as a new revision. Returns the
	/// new revision id. Throws on FK violations (page doesn't exist,
	/// chunk doesn't exist) and on uniqueness violations
	/// (<c>revision_number</c> already used for this facet).
	/// </summary>
	Task<Guid> CommitAsync(WikiRevisionDraft draft, CancellationToken cancellationToken = default);
}

/// <summary>
/// The input shape <see cref="IWikiRevisionWriter.CommitAsync"/>
/// expects. One draft commits one revision with N claims and M
/// citations.
/// </summary>
/// <param name="PageId">Existing <c>wiki_pages.id</c>.</param>
/// <param name="MinClassification">Facet classification ceiling.</param>
/// <param name="PersonaId">Persona facet variant; null = persona-neutral.</param>
/// <param name="RevisionNumber">Monotonic per-facet revno. Caller computes <c>max(existing) + 1</c>.</param>
/// <param name="AuthoredBy"><c>users.id</c> of the author; system sentinel for autonomous writes.</param>
/// <param name="BodyMarkdown">Assembled body at commit time. Should NOT contain inline citation tokens; those live in <see cref="Claims"/>.</param>
/// <param name="Claims">Claims + citations to attach to this revision.</param>
public sealed record WikiRevisionDraft(
	Guid PageId,
	Classification MinClassification,
	Guid? PersonaId,
	int RevisionNumber,
	Guid AuthoredBy,
	string BodyMarkdown,
	IReadOnlyList<WikiClaimDraft> Claims);

/// <summary>One claim row to be inserted, with its citation rows.</summary>
/// <param name="ClaimText">The claim sentence/fragment.</param>
/// <param name="Position">0-based order within the revision.</param>
/// <param name="Citations">Per-claim citations, all into <c>source_chunks</c>.</param>
public sealed record WikiClaimDraft(
	string ClaimText,
	int Position,
	IReadOnlyList<Citation> Citations);
