using AiLibrarian.Domain;

namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// Write-side surface for the auto-page-discovery flow. Operators
/// (Admin) submit a candidate <c>(department, slug, title, facet)</c>
/// tuple via <c>POST /api/admin/wiki/discover</c>; the implementation
/// idempotently materializes the <c>wiki_pages</c> + <c>page_facets</c>
/// rows so the maintenance pass can land a revision on the same call.
///
/// <para>Closes the runbook gap that previously required operators to
/// hand-write two <c>INSERT</c> statements before each maintenance call.
/// The writer runs in system admin RLS context (same pattern as
/// <see cref="IWikiPageReader"/>); the API-layer handler is what
/// authorizes the caller (Admin-only).</para>
///
/// <para>Idempotency: when a page with the same
/// <c>(department_id, slug)</c> already exists, the writer returns its
/// id without modifying it (no title overwrite, no locked-flag reset).
/// When the page exists but the requested facet doesn't, only the facet
/// row is inserted. Both flags on
/// <see cref="EnsurePageResult"/> distinguish "found" from "created" so
/// the caller can surface a 200 vs. 201 if it wants.</para>
/// </summary>
public interface IWikiPageWriter
{
	/// <summary>
	/// Ensure the requested page + facet exist. See type-level docs for
	/// idempotency semantics.
	/// </summary>
	Task<EnsurePageResult> EnsurePageAsync(
		EnsurePageRequest request,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Update the human-readable title on an existing page. Slug stays
	/// frozen — operators rename pages "in name" only; the canonical
	/// identity (department + slug) is permanent. Returns
	/// <see langword="true"/> when a row was updated,
	/// <see langword="false"/> when the page id wasn't found.
	/// </summary>
	Task<bool> RenameAsync(
		Guid pageId,
		string newTitle,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Flip the <c>locked</c> flag on a page. Locking routes future
	/// maintenance into the approval queue (ADR 0006 Q13); unlocking
	/// restores direct-commit. Returns <see langword="true"/> when the
	/// row was updated. Idempotent: setting the flag to its current
	/// value returns <see langword="true"/> (the row exists) without
	/// emitting a no-op write.
	/// </summary>
	Task<bool> SetLockedAsync(
		Guid pageId,
		bool locked,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Soft-delete a page per ADR 0008 (Tier-1 deletion). Sets
	/// <c>wiki_pages.soft_deleted_at</c> to <c>now()</c>; the row stays
	/// in the table for audit but is hidden from every RLS-filtered
	/// read. Downstream rows (facets, revisions, claims, citations)
	/// follow transitively because their read predicates check the
	/// parent page through <c>EXISTS</c>.
	///
	/// <para>Returns <see langword="true"/> when this call transitioned
	/// a live page to soft-deleted, <see langword="false"/> when the
	/// page doesn't exist OR is already soft-deleted (idempotent
	/// no-op). Slug becomes free for reuse — <see cref="EnsurePageAsync"/>
	/// will create a fresh row with the same <c>(department_id, slug)</c>
	/// because the live-only partial unique index allows it.</para>
	/// </summary>
	Task<bool> SoftDeleteAsync(
		Guid pageId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Undo a prior soft-delete: clear <c>wiki_pages.soft_deleted_at</c>
	/// so the row becomes visible to RLS again. Three outcomes
	/// surfaced via <see cref="RestorePageOutcome"/>:
	/// <list type="bullet">
	///   <item><c>Restored</c> — the row was soft-deleted and is now
	///         live again.</item>
	///   <item><c>NotFound</c> — no soft-deleted row with this id
	///         exists (either the id is unknown OR the row is already
	///         live; both surface as 404 at the API).</item>
	///   <item><c>SlugConflict</c> — restoring would violate the
	///         live-only partial unique index because a different
	///         live row already holds the same
	///         <c>(department_id, slug)</c>. The conflicting page's
	///         id is carried on the result so the operator can
	///         coordinate (rename or re-delete the replacement
	///         before restoring).</item>
	/// </list>
	/// </summary>
	Task<RestorePageResult> RestoreAsync(
		Guid pageId,
		CancellationToken cancellationToken = default);
}

/// <summary>Three-way outcome from <see cref="IWikiPageWriter.RestoreAsync"/>.</summary>
public enum RestorePageOutcome
{
	/// <summary>The soft-deleted row exists and was cleared back to live.</summary>
	Restored = 0,

	/// <summary>No soft-deleted row with this id exists (unknown or already live).</summary>
	NotFound = 1,

	/// <summary>A different live row holds the same (department_id, slug); restoring would violate the partial unique index.</summary>
	SlugConflict = 2,
}

/// <summary>Carries the outcome of a restore attempt plus the conflicting page id when relevant.</summary>
/// <param name="Outcome">Three-way outcome.</param>
/// <param name="ConflictingLivePageId">Id of the live page already holding the slug — non-null only when <see cref="Outcome"/> is <see cref="RestorePageOutcome.SlugConflict"/>.</param>
public sealed record RestorePageResult(
	RestorePageOutcome Outcome,
	Guid? ConflictingLivePageId);

/// <summary>Input shape for <see cref="IWikiPageWriter.EnsurePageAsync"/>.</summary>
/// <param name="DepartmentId">Owning department; FK on <c>wiki_pages.department_id</c>.</param>
/// <param name="Slug">URL-safe slug. Must match <c>^[a-z0-9][a-z0-9\-]{0,254}$</c> (the DB check constraint); use <see cref="WikiSlug.From(string)"/> to derive one from a title.</param>
/// <param name="Title">Human-readable title; written on first creation, never overwritten on subsequent calls.</param>
/// <param name="FacetClassification">Classification of the facet to ensure.</param>
/// <param name="PersonaId">Optional persona id; null = persona-neutral facet.</param>
public sealed record EnsurePageRequest(
	Guid DepartmentId,
	string Slug,
	string Title,
	Classification FacetClassification,
	Guid? PersonaId);

/// <summary>Outcome of an <see cref="IWikiPageWriter.EnsurePageAsync"/> call.</summary>
/// <param name="PageId">Id of the page (newly created or pre-existing).</param>
/// <param name="PageCreated">True when this call inserted the <c>wiki_pages</c> row.</param>
/// <param name="FacetCreated">True when this call inserted the <c>page_facets</c> row.</param>
public sealed record EnsurePageResult(
	Guid PageId,
	bool PageCreated,
	bool FacetCreated);
