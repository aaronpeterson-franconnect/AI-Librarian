namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// Read-side lookup the Wiki Maintainer uses to check whether the
/// target page is locked. When locked, the maintainer routes the
/// would-be revision into the approval queue instead of committing
/// directly (ADR 0006).
///
/// <para>Kept minimal on purpose — the full page-management surface
/// (list, create, soft-delete) is a future slice; the maintainer
/// only needs "tell me if this id is locked."</para>
/// </summary>
public interface IWikiPageReader
{
	/// <summary>
	/// Return true when the page exists AND its <c>locked</c> column
	/// is true. Returns false when the page doesn't exist (the
	/// maintainer surfaces that as a separate error path via the
	/// downstream FK violation).
	/// </summary>
	Task<bool> IsLockedAsync(Guid pageId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Enumerate the slugs of every existing wiki page in the supplied
	/// department. Used by the candidate-discovery flow to dedupe
	/// LLM-proposed slugs against pages that already exist — operators
	/// don't want "Ingest Worker" suggested again when there's already
	/// an <c>ingest-worker</c> page. Runs in system admin context so
	/// the visibility is whole-department regardless of caller role.
	/// </summary>
	Task<IReadOnlySet<string>> ListSlugsAsync(
		Guid departmentId,
		CancellationToken cancellationToken = default);
}
