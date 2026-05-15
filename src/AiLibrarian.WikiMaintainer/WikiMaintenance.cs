using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;

namespace AiLibrarian.WikiMaintainer;

/// <summary>
/// One invocation of <see cref="IWikiMaintainer.GenerateRevisionAsync"/>.
/// The caller pre-creates the page + facet rows (the Maintainer
/// doesn't decide what pages exist) and supplies a chunk pool. The
/// Maintainer asks the LLM to synthesize using that pool, then
/// extracts + validates + commits.
/// </summary>
/// <param name="PageId">Existing <c>wiki_pages.id</c>.</param>
/// <param name="FacetClassification">Facet's classification ceiling.</param>
/// <param name="PersonaId">Persona facet variant; null = persona-neutral.</param>
/// <param name="RevisionNumber">Next per-facet revision number (caller computes).</param>
/// <param name="Topic">What the page is about. Drives the Pass 1 LLM prompt.</param>
/// <param name="SourceChunks">RLS-filtered chunks the LLM may cite.</param>
/// <param name="AuthoredBy">Will be stamped as <c>authored_by</c> on the revision row; typically the system sentinel.</param>
public sealed record WikiMaintenanceRequest(
	Guid PageId,
	Classification FacetClassification,
	Guid? PersonaId,
	int RevisionNumber,
	string Topic,
	IReadOnlyList<WikiMaintenanceSourceChunk> SourceChunks,
	Guid AuthoredBy);

/// <summary>
/// One chunk in the Wiki Maintainer's source pool. The Maintainer
/// emits the chunk's text in the Pass 1 prompt and accepts citations
/// against its id.
/// </summary>
/// <param name="ChunkId">Stable chunk identifier.</param>
/// <param name="ContentMarkdown">Canonical chunk content (what the LLM sees).</param>
/// <param name="Classification">Chunk's classification (the validator's rule 4 checks this against the facet ceiling).</param>
public sealed record WikiMaintenanceSourceChunk(
	Guid ChunkId,
	string ContentMarkdown,
	Classification Classification);

/// <summary>
/// Outcome of one Wiki Maintainer pass.
/// </summary>
/// <param name="Succeeded">True when the revision was committed; false when validation rejected it (or no claims emerged).</param>
/// <param name="RevisionId">New revision id; null when <see cref="Succeeded"/> is false.</param>
/// <param name="BodyMarkdown">The Pass 1 prose with inline citation tokens stripped.</param>
/// <param name="ClaimCount">How many claims the Pass 2 extractor produced.</param>
/// <param name="CitationCount">How many citation rows it produced.</param>
/// <param name="ValidationResult">The full <see cref="CitationValidationResult"/>. Examine on rejection to find which rule(s) fired.</param>
/// <param name="RejectionReason">Human-readable failure summary; null on success.</param>
public sealed record WikiMaintenanceResult(
	bool Succeeded,
	Guid? RevisionId,
	string BodyMarkdown,
	int ClaimCount,
	int CitationCount,
	CitationValidationResult ValidationResult,
	string? RejectionReason);
