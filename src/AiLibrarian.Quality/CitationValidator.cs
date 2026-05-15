using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Quality;

/// <summary>
/// Mechanical citation validator. Applies the five rules from ADR 0007
/// against a batch of claims:
/// <list type="number">
///   <item>Every claim has ≥1 citation.</item>
///   <item>Every citation resolves to a non-deleted chunk.</item>
///   <item>The citation span lies within the chunk's bounds.</item>
///   <item><c>chunk.classification &lt;= facet.classification</c>.</item>
///   <item><c>citation.confidence &gt;= configured floor</c>.</item>
/// </list>
/// Rules are independent — failing rule 2 still records rule-3/4/5
/// violations against the same citation when they'd also have failed,
/// so the caller sees the full picture in one pass.
/// </summary>
public sealed class CitationValidator : ICitationValidator
{
	private readonly IChunkLookup _chunks;
	private readonly CitationValidatorOptions _options;
	private readonly ILogger<CitationValidator> _logger;

	/// <summary>Creates the validator. Options + logger are optional for callers wiring it manually (tests, eval harness).</summary>
	public CitationValidator(
		IChunkLookup chunks,
		IOptions<CitationValidatorOptions>? options = null,
		ILogger<CitationValidator>? logger = null)
	{
		_chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
		_options = options?.Value ?? new CitationValidatorOptions();
		_logger = logger ?? NullLogger<CitationValidator>.Instance;
	}

	/// <inheritdoc />
	public async Task<CitationValidationResult> ValidateAsync(
		IReadOnlyList<Claim> claims,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(claims);

		if (claims.Count == 0)
		{
			return new CitationValidationResult(Array.Empty<CitationViolation>());
		}

		// Resolve every cited chunk in one batch so the validator stays
		// O(1) database round trips per call. Missing chunks legitimately
		// don't appear in the dictionary — that's the rule-2 signal.
		var chunkIds = new HashSet<Guid>();
		foreach (var claim in claims)
		{
			foreach (var citation in claim.Citations)
			{
				chunkIds.Add(citation.ChunkId);
			}
		}

		IReadOnlyDictionary<Guid, ChunkRef> resolved = chunkIds.Count == 0
			? new Dictionary<Guid, ChunkRef>()
			: await _chunks.ResolveAsync(chunkIds, cancellationToken).ConfigureAwait(false);

		var violations = new List<CitationViolation>();

		foreach (var claim in claims)
		{
			// Rule 1 — claim has at least one citation.
			if (claim.Citations.Count == 0)
			{
				violations.Add(new CitationViolation(
					claim.Id,
					CitationId: null,
					CitationRule.ClaimHasCitation,
					"Claim has no citations."));
				continue;
			}

			foreach (var citation in claim.Citations)
			{
				ValidateCitation(claim, citation, resolved, violations);
			}
		}

		if (violations.Count > 0)
		{
			_logger.LogInformation(
				"CitationValidator: {Count} violation(s) across {ClaimCount} claim(s).",
				violations.Count,
				claims.Count);
		}

		return new CitationValidationResult(violations);
	}

	private void ValidateCitation(
		Claim claim,
		Citation citation,
		IReadOnlyDictionary<Guid, ChunkRef> resolved,
		List<CitationViolation> violations)
	{
		// Rule 5 — confidence floor — runs independent of chunk resolution.
		if (citation.Confidence < _options.ConfidenceFloor)
		{
			violations.Add(new CitationViolation(
				claim.Id,
				citation.Id,
				CitationRule.ConfidenceFloorMet,
				$"Confidence {citation.Confidence:F3} below floor {_options.ConfidenceFloor:F3}."));
		}

		// Rule 2 — chunk resolves at all? Missing-from-dictionary means
		// the chunk row was hard-deleted or never existed (also covers
		// the "dangling after cascade" case until the cascade worker is
		// in place to soft-delete + re-grade).
		if (!resolved.TryGetValue(citation.ChunkId, out var chunk))
		{
			violations.Add(new CitationViolation(
				claim.Id,
				citation.Id,
				CitationRule.ChunkResolves,
				$"Chunk {citation.ChunkId:D} not found."));
			return;
		}

		if (chunk.IsSoftDeleted)
		{
			violations.Add(new CitationViolation(
				claim.Id,
				citation.Id,
				CitationRule.ChunkResolves,
				$"Chunk {citation.ChunkId:D} is soft-deleted."));
			// Don't short-circuit — rules 3 and 4 still produce useful
			// signal for the librarian dashboard ("which surfaces would
			// have leaked if RLS had failed open").
		}

		// Rule 3 — span within chunk. SpanEnd is exclusive, so equal-to-length is fine.
		if (citation.SpanStart < 0
			|| citation.SpanEnd <= citation.SpanStart
			|| citation.SpanEnd > chunk.ContentLength)
		{
			violations.Add(new CitationViolation(
				claim.Id,
				citation.Id,
				CitationRule.SpanWithinChunk,
				$"Span [{citation.SpanStart},{citation.SpanEnd}) out of bounds for chunk length {chunk.ContentLength}."));
		}

		// Rule 4 — leakage. The lattice helper enforces cited <= facet.
		if (!ClassificationLattice.MayCite(claim.FacetClassification, chunk.Classification))
		{
			violations.Add(new CitationViolation(
				claim.Id,
				citation.Id,
				CitationRule.ClassificationNotLeaking,
				$"Cited chunk classification {chunk.Classification} exceeds facet ceiling {claim.FacetClassification}."));
		}
	}
}
