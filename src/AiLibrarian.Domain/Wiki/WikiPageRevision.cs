namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// One row in <c>wiki_page_revisions</c> — a committed revision of one
/// facet of one page per ADR 0007. Each (page, classification, persona)
/// facet has its own monotonic <see cref="RevisionNumber"/> sequence.
/// </summary>
/// <param name="Id">Stable revision id; claims FK on this.</param>
/// <param name="PageId">Parent wiki page.</param>
/// <param name="MinClassification">Facet's classification ceiling.</param>
/// <param name="PersonaId">Persona facet variant (null = persona-neutral).</param>
/// <param name="RevisionNumber">Monotonic per-facet (page, classification, persona) revno.</param>
/// <param name="AuthoredBy">
/// <see cref="Users.UserRow.Id"/> of the author. Autonomous Wiki
/// Maintainer writes use the system sentinel user id.
/// </param>
/// <param name="AuthoredAt">Revision-commit timestamp.</param>
/// <param name="BodyMarkdown">Assembled body at commit time (snapshot).</param>
public sealed record WikiPageRevision(
	Guid Id,
	Guid PageId,
	Classification MinClassification,
	Guid? PersonaId,
	int RevisionNumber,
	Guid AuthoredBy,
	DateTimeOffset AuthoredAt,
	string BodyMarkdown);
