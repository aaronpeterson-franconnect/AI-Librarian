namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// One row in <c>wiki_pages</c> — the LLM-authored canonical document
/// per ADR 0006. Pages are department-scoped; slugs are unique within
/// a department.
/// </summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="DepartmentId">Owning department (corpus boundary, ADR 0005).</param>
/// <param name="Slug">URL-safe lowercase identifier; unique per department.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Locked">
/// When true, writes go through the proposed-revision approval queue
/// (Phase 2.5). The Wiki Maintainer still produces revisions, but they
/// land as proposals; a Reviewer/Librarian decides whether to accept.
/// </param>
/// <param name="CreatedAt">Provisioning timestamp.</param>
/// <param name="UpdatedAt">Last update timestamp (any facet update bumps this).</param>
public sealed record WikiPage(
	Guid Id,
	Guid DepartmentId,
	string Slug,
	string Title,
	bool Locked,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
