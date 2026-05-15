namespace AiLibrarian.WikiMaintainer;

/// <summary>
/// Replaces the per-citation confidence stamped by
/// <see cref="Pass2CitationExtractor"/> with a real signal. Two
/// implementations:
/// <list type="bullet">
///   <item><see cref="PlaceholderConfidenceScorer"/> — returns claims
///         unchanged. The Pass-2 placeholder value (default 0.85) flows
///         through; matches the pre-scoring behavior.</item>
///   <item><c>EmbeddingSimilarityConfidenceScorer</c> — embeds each
///         claim text + each source chunk's content, computes
///         cosine similarity per (claim, citation), uses that as the
///         confidence.</item>
/// </list>
///
/// <para>Scoring runs AFTER Pass 2 and BEFORE
/// <see cref="Domain.Citations.ICitationValidator"/>. Validator rule 5
/// (confidence floor) then operates on the real values.</para>
///
/// <para>Pass 2 stays sync + deterministic; this hook is the async
/// LLM-bound step. Both Pass 2 and the scorer are decoupled from the
/// validator so each layer can be unit-tested in isolation.</para>
/// </summary>
public interface IConfidenceScorer
{
	/// <summary>
	/// Re-score every citation in <paramref name="claims"/> using the
	/// supplied <paramref name="sourcePool"/>. Returns a new list with
	/// updated <see cref="Domain.Citations.Citation.Confidence"/>
	/// values; the rest of the claim/citation shape is preserved.
	/// Citations whose chunk isn't in the pool keep their input
	/// confidence (defensive; the pass-2 contract drops out-of-pool
	/// citations upstream).
	/// </summary>
	Task<IReadOnlyList<Domain.Wiki.WikiClaimDraft>> ScoreAsync(
		IReadOnlyList<Domain.Wiki.WikiClaimDraft> claims,
		IReadOnlyList<WikiMaintenanceSourceChunk> sourcePool,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op <see cref="IConfidenceScorer"/>. Returns claims unchanged so
/// the Pass-2 placeholder value flows through. The default when
/// embedding-similarity scoring isn't configured; deployments swap
/// to the real one via DI.
/// </summary>
public sealed class PlaceholderConfidenceScorer : IConfidenceScorer
{
	/// <inheritdoc />
	public Task<IReadOnlyList<Domain.Wiki.WikiClaimDraft>> ScoreAsync(
		IReadOnlyList<Domain.Wiki.WikiClaimDraft> claims,
		IReadOnlyList<WikiMaintenanceSourceChunk> sourcePool,
		CancellationToken cancellationToken = default)
		=> Task.FromResult(claims);
}
