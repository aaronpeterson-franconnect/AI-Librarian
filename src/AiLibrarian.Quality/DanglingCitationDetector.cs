using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Quality;

/// <summary>
/// Sweeps a batch of citations against the current chunk lookup and
/// reports the ones that no longer resolve to a live chunk (hard-deleted
/// or soft-deleted). Phase 4's Cascade-Regeneration Worker will call
/// this on a schedule and re-grade affected surfaces; the detector
/// itself is just the read-side query so we can land it today.
/// </summary>
public sealed class DanglingCitationDetector
{
	private readonly IChunkLookup _chunks;

	/// <summary>Creates the detector.</summary>
	public DanglingCitationDetector(IChunkLookup chunks)
	{
		_chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
	}

	/// <summary>
	/// Resolve every cited chunk and return the citations whose chunk is
	/// missing or soft-deleted. The result is deduplicated by citation
	/// id; the same chunk turning dangling produces one row per affected
	/// citation, not one row per cited claim.
	/// </summary>
	public async Task<IReadOnlyList<DanglingCitation>> FindAsync(
		IReadOnlyList<Citation> citations,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(citations);

		if (citations.Count == 0)
		{
			return Array.Empty<DanglingCitation>();
		}

		var chunkIds = new HashSet<Guid>();
		foreach (var c in citations)
		{
			chunkIds.Add(c.ChunkId);
		}

		var resolved = await _chunks.ResolveAsync(chunkIds, cancellationToken).ConfigureAwait(false);

		var result = new List<DanglingCitation>();
		foreach (var citation in citations)
		{
			if (!resolved.TryGetValue(citation.ChunkId, out var chunk))
			{
				result.Add(new DanglingCitation(citation.Id, citation.ChunkId, DanglingReason.ChunkMissing));
			}
			else if (chunk.IsSoftDeleted)
			{
				result.Add(new DanglingCitation(citation.Id, citation.ChunkId, DanglingReason.ChunkSoftDeleted));
			}
		}

		return result;
	}
}

/// <summary>One row in <see cref="DanglingCitationDetector.FindAsync"/>'s result.</summary>
/// <param name="CitationId">The dangling citation.</param>
/// <param name="ChunkId">The chunk it pointed at.</param>
/// <param name="Reason">Why the citation is dangling.</param>
public sealed record DanglingCitation(Guid CitationId, Guid ChunkId, DanglingReason Reason);

/// <summary>Reasons a citation can be dangling.</summary>
public enum DanglingReason
{
	/// <summary>The chunk row is gone (hard-deleted or never existed).</summary>
	ChunkMissing = 0,

	/// <summary>The chunk row exists but its owning source is soft-deleted.</summary>
	ChunkSoftDeleted = 1,
}
