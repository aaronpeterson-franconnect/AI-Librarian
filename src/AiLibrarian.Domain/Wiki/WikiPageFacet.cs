namespace AiLibrarian.Domain.Wiki;

/// <summary>
/// One row in <c>page_facets</c> — a (page, classification, persona)
/// cell per ADR 0006 amendment. The composite key uses
/// <c>COALESCE(persona_id, '00000000-...-0000-0')</c> so the persona-
/// neutral facet (null persona) coexists with persona-specific facets
/// for the same (page, classification) pair.
/// </summary>
/// <param name="PageId">Parent wiki page.</param>
/// <param name="MinClassification">Minimum classification a reader must clear.</param>
/// <param name="PersonaId">Null = persona-neutral facet; non-null = persona-shaped variant.</param>
/// <param name="BodyMarkdown">Assembled body for rendering (constructed from claims).</param>
/// <param name="CurrentRevisionId">FK into <c>wiki_page_revisions</c>; null until first revision lands.</param>
/// <param name="CreatedAt">Facet creation timestamp.</param>
/// <param name="UpdatedAt">Last revision-commit timestamp.</param>
public sealed record WikiPageFacet(
	Guid PageId,
	Classification MinClassification,
	Guid? PersonaId,
	string BodyMarkdown,
	Guid? CurrentRevisionId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
