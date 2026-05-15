using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.LlmGateway.Abstractions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.WikiMaintainer;

/// <summary>
/// <see cref="IConfidenceScorer"/> backed by an
/// <see cref="IEmbeddingProvider"/>. For each (claim, citation) tuple,
/// computes cosine similarity between the claim text's embedding and
/// the cited chunk's content embedding; clamps to [0, 1]; uses the
/// result as the citation's confidence.
///
/// <para>Cost-aware: two batched embedding calls per scoring pass
/// (one for all distinct claim texts, one for all distinct chunk
/// texts) regardless of how many citations exist. For a typical
/// page (10 claims × 20 chunks) that's 30 embedding inputs in 2
/// API calls.</para>
///
/// <para>If the embedding call fails, falls back to the citation's
/// existing confidence value rather than throwing — a transient
/// embedding outage shouldn't fail every maintenance call. The
/// fallback is logged and surfaced via the rejection-reason path if
/// the resulting confidences trip rule 5.</para>
/// </summary>
public sealed class EmbeddingSimilarityConfidenceScorer : IConfidenceScorer
{
	private readonly IEmbeddingProvider _embeddings;
	private readonly EmbeddingSimilarityScorerOptions _options;
	private readonly ILogger<EmbeddingSimilarityConfidenceScorer> _logger;

	/// <summary>Creates the scorer.</summary>
	public EmbeddingSimilarityConfidenceScorer(
		IEmbeddingProvider embeddings,
		IOptions<EmbeddingSimilarityScorerOptions>? options = null,
		ILogger<EmbeddingSimilarityConfidenceScorer>? logger = null)
	{
		_embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
		_options = options?.Value ?? new EmbeddingSimilarityScorerOptions();
		_logger = logger ?? NullLogger<EmbeddingSimilarityConfidenceScorer>.Instance;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<WikiClaimDraft>> ScoreAsync(
		IReadOnlyList<WikiClaimDraft> claims,
		IReadOnlyList<WikiMaintenanceSourceChunk> sourcePool,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(claims);
		ArgumentNullException.ThrowIfNull(sourcePool);

		if (claims.Count == 0 || sourcePool.Count == 0)
		{
			return claims;
		}

		if (string.IsNullOrWhiteSpace(_options.EmbeddingDeployment))
		{
			_logger.LogWarning("EmbeddingSimilarityConfidenceScorer: deployment not set; returning claims unchanged.");
			return claims;
		}

		// Distinct claim texts so two claims with the same text share an
		// embedding (rare but cheap to dedupe).
		var distinctClaimTexts = claims
			.Select(c => c.ClaimText)
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		var chunksById = sourcePool.ToDictionary(c => c.ChunkId);

		// Only embed chunks that are actually cited; saves API cost on
		// big retrieval pools where most chunks weren't ultimately
		// referenced.
		var citedChunkIds = claims
			.SelectMany(c => c.Citations)
			.Select(c => c.ChunkId)
			.Where(chunksById.ContainsKey)
			.Distinct()
			.ToArray();

		if (citedChunkIds.Length == 0)
		{
			return claims;
		}

		IReadOnlyList<ReadOnlyMemory<float>> claimVectors;
		IReadOnlyList<ReadOnlyMemory<float>> chunkVectors;

		try
		{
			var correlationId = Guid.NewGuid();
			claimVectors = await _embeddings
				.EmbedAsync(_options.EmbeddingDeployment, distinctClaimTexts, correlationId, cancellationToken)
				.ConfigureAwait(false);

			var chunkTexts = citedChunkIds
				.Select(id => chunksById[id].ContentMarkdown ?? string.Empty)
				.ToArray();

			chunkVectors = await _embeddings
				.EmbedAsync(_options.EmbeddingDeployment, chunkTexts, correlationId, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogError(ex,
				"EmbeddingSimilarityConfidenceScorer: embedding call failed; returning claims with placeholder confidence.");
			return claims;
		}

		if (claimVectors.Count != distinctClaimTexts.Length || chunkVectors.Count != citedChunkIds.Length)
		{
			_logger.LogWarning(
				"EmbeddingSimilarityConfidenceScorer: embedding provider returned {GotClaims}/{ExpClaims} claim and {GotChunks}/{ExpChunks} chunk vectors; returning claims unchanged.",
				claimVectors.Count, distinctClaimTexts.Length, chunkVectors.Count, citedChunkIds.Length);
			return claims;
		}

		// Index lookups.
		var claimVectorByText = new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);
		for (var i = 0; i < distinctClaimTexts.Length; i++)
		{
			claimVectorByText[distinctClaimTexts[i]] = claimVectors[i];
		}

		var chunkVectorById = new Dictionary<Guid, ReadOnlyMemory<float>>();
		for (var i = 0; i < citedChunkIds.Length; i++)
		{
			chunkVectorById[citedChunkIds[i]] = chunkVectors[i];
		}

		// Rebuild claims with scored citations.
		var scored = new WikiClaimDraft[claims.Count];
		for (var i = 0; i < claims.Count; i++)
		{
			var claim = claims[i];
			if (!claimVectorByText.TryGetValue(claim.ClaimText, out var claimVec))
			{
				scored[i] = claim;
				continue;
			}

			var newCitations = new Citation[claim.Citations.Count];
			for (var j = 0; j < claim.Citations.Count; j++)
			{
				var citation = claim.Citations[j];
				if (!chunkVectorById.TryGetValue(citation.ChunkId, out var chunkVec))
				{
					newCitations[j] = citation;
					continue;
				}

				var similarity = CosineSimilarity(claimVec.Span, chunkVec.Span);
				// Cosine similarity for non-unit vectors is in [-1, 1].
				// Negative means the embeddings point opposite ways --
				// treat as 0 (no signal). Then clamp to [0, 1].
				var confidence = Math.Clamp(similarity, 0.0, 1.0);
				newCitations[j] = citation with { Confidence = confidence };
			}

			scored[i] = claim with { Citations = newCitations };
		}

		return scored;
	}

	private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
	{
		if (a.Length != b.Length || a.Length == 0)
		{
			return 0.0;
		}

		double dot = 0.0;
		double normA = 0.0;
		double normB = 0.0;
		for (var i = 0; i < a.Length; i++)
		{
			dot += a[i] * b[i];
			normA += a[i] * a[i];
			normB += b[i] * b[i];
		}

		if (normA <= double.Epsilon || normB <= double.Epsilon)
		{
			return 0.0;
		}

		return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
	}
}

/// <summary>Knobs for <see cref="EmbeddingSimilarityConfidenceScorer"/>.</summary>
public sealed class EmbeddingSimilarityScorerOptions
{
	/// <summary>Configuration section name (<c>WikiMaintainer:EmbeddingScorer</c>).</summary>
	public const string SectionName = "WikiMaintainer:EmbeddingScorer";

	/// <summary>
	/// Embedding deployment name (Azure OpenAI deployment id or
	/// provider-specific model id). Defaults to empty so the
	/// scorer silently falls through to the placeholder until the
	/// operator opts in by setting this.
	/// </summary>
	public string EmbeddingDeployment { get; set; } = string.Empty;
}
