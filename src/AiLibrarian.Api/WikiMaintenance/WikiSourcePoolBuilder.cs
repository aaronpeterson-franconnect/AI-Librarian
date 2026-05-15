using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Infrastructure.Retrieval;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.LlmGateway;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.WikiMaintainer;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.WikiMaintenance;

/// <summary>
/// Contract for the source-pool build step used by the maintain +
/// discover admin endpoints and the cascade-regeneration hosted
/// service. Extracted as an interface so handler-level tests can stub
/// the embedding + hybrid-search + content-reader plumbing; the
/// production implementation is <see cref="WikiSourcePoolBuilder"/>.
/// </summary>
public interface IWikiSourcePoolBuilder
{
	/// <summary>
	/// Embed <paramref name="query"/>, run hybrid retrieval under
	/// <paramref name="rlsContext"/>, and adapt the hits into the
	/// shape the Wiki Maintainer expects.
	/// </summary>
	Task<WikiSourcePoolResult> BuildAsync(
		RlsSessionContext rlsContext,
		string query,
		CancellationToken cancellationToken);
}

/// <summary>
/// Builds the source-chunk pool the Wiki Maintainer feeds into Pass 1.
/// Both the on-demand maintain endpoint and the cascade-regeneration
/// worker route through here, so the LLM sees a consistent shape no
/// matter who triggered the run.
///
/// <para>Uses the same <see cref="IHybridChunkSearch"/> that powers
/// <c>/api/search/hybrid</c>; the maintainer's RLS context passes
/// straight through, so a sweep that targets the Engineering
/// department only ever sees Engineering chunks.</para>
///
/// <para>The hybrid hit's <c>Excerpt</c> is truncated at 600 chars
/// for retrieval speed. After retrieval we upgrade each hit's content
/// to the full canonical markdown via
/// <see cref="IChunkContentReader"/>, capped at
/// <see cref="WikiMaintenanceOptions.MaxChunkContentChars"/>. Set the
/// cap to 600 to skip the upgrade and keep the excerpts.</para>
/// </summary>
public sealed class WikiSourcePoolBuilder : IWikiSourcePoolBuilder
{
	private readonly IHybridChunkSearch _hybrid;
	private readonly IEmbeddingProvider _embeddings;
	private readonly IChunkContentReader _contentReader;
	private readonly IOptions<LlmGatewayOptions> _llmOptions;
	private readonly IOptions<SearchOptions> _searchOptions;
	private readonly IOptions<WikiMaintenanceOptions> _maintenanceOptions;
	private readonly ILogger<WikiSourcePoolBuilder> _logger;

	/// <summary>Creates the builder.</summary>
	public WikiSourcePoolBuilder(
		IHybridChunkSearch hybrid,
		IEmbeddingProvider embeddings,
		IChunkContentReader contentReader,
		IOptions<LlmGatewayOptions> llmOptions,
		IOptions<SearchOptions> searchOptions,
		IOptions<WikiMaintenanceOptions> maintenanceOptions,
		ILogger<WikiSourcePoolBuilder> logger)
	{
		_hybrid = hybrid;
		_embeddings = embeddings;
		_contentReader = contentReader;
		_llmOptions = llmOptions;
		_searchOptions = searchOptions;
		_maintenanceOptions = maintenanceOptions;
		_logger = logger;
	}

	/// <summary>
	/// Embed <paramref name="query"/>, run hybrid retrieval under
	/// <paramref name="rlsContext"/>, and adapt the hits into the
	/// shape the Wiki Maintainer expects.
	/// </summary>
	/// <returns>The pool plus the embedding deployment used (so the caller can stamp it in audit details).</returns>
	public async Task<WikiSourcePoolResult> BuildAsync(
		RlsSessionContext rlsContext,
		string query,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(rlsContext);
		ArgumentException.ThrowIfNullOrWhiteSpace(query);

		var deploy = ResolveEmbeddingDeployment(_searchOptions.Value, _llmOptions.Value);
		if (string.IsNullOrWhiteSpace(deploy))
		{
			throw new InvalidOperationException(
				"Search:EmbeddingDeployment (or LlmGateway:Providers:azure-openai:EmbeddingDeployment) must be set for wiki maintenance.");
		}

		var vectors = await _embeddings
			.EmbedAsync(deploy, new[] { query }, Guid.NewGuid(), cancellationToken)
			.ConfigureAwait(false);

		if (vectors.Count != 1 || vectors[0].Length != _searchOptions.Value.ExpectedEmbeddingDimensions)
		{
			throw new InvalidOperationException(
				$"Embedding provider returned an unexpected response shape. Expected one vector of length {_searchOptions.Value.ExpectedEmbeddingDimensions}.");
		}

		var opts = _maintenanceOptions.Value;
		var hits = await _hybrid
			.SearchAsync(
				rlsContext,
				query,
				vectors[0],
				new HybridSearchRequestOptions(opts.RetrievalLimit, opts.HybridVectorWeight),
				cancellationToken)
			.ConfigureAwait(false);

		// Phase 2 upgrade: pull full canonical content per hit so Pass 1
		// sees more than the 600-char excerpt. Skipped when the cap is
		// <= 600 or no hits came back. Failures fall through to the
		// excerpt so a transient Postgres hiccup doesn't fail the run.
		var cap = opts.MaxChunkContentChars;
		IReadOnlyDictionary<Guid, string> fullContent;
		if (cap > 600 && hits.Count > 0)
		{
			try
			{
				var ids = hits.Select(h => h.ChunkId).Distinct().ToArray();
				fullContent = await _contentReader
					.ReadContentAsync(ids, maxCharsPerChunk: cap, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogWarning(ex,
					"Chunk-content upgrade failed; falling back to retrieval excerpts (600 chars each).");
				fullContent = new Dictionary<Guid, string>();
			}
		}
		else
		{
			fullContent = new Dictionary<Guid, string>();
		}

		var pool = new WikiMaintenanceSourceChunk[hits.Count];
		var upgraded = 0;
		for (var i = 0; i < hits.Count; i++)
		{
			var hit = hits[i];
			var content = fullContent.TryGetValue(hit.ChunkId, out var full) && !string.IsNullOrEmpty(full)
				? full
				: hit.Excerpt ?? string.Empty;
			if (fullContent.ContainsKey(hit.ChunkId))
			{
				upgraded++;
			}

			pool[i] = new WikiMaintenanceSourceChunk(
				ChunkId: hit.ChunkId,
				ContentMarkdown: content,
				Classification: hit.SourceClassification);
		}

		_logger.LogDebug(
			"WikiSourcePoolBuilder: query=\"{Query:l}\" hits={Hits} upgraded={Upgraded} cap={Cap} embedding={Deployment}",
			query.Length > 80 ? query[..80] + "..." : query,
			hits.Count,
			upgraded,
			cap,
			deploy);

		return new WikiSourcePoolResult(pool, deploy);
	}

	private static string ResolveEmbeddingDeployment(SearchOptions search, LlmGatewayOptions llm)
	{
		if (!string.IsNullOrWhiteSpace(search.EmbeddingDeployment))
		{
			return search.EmbeddingDeployment;
		}

		return llm.Providers.TryGetValue("azure-openai", out var az) && !string.IsNullOrWhiteSpace(az.EmbeddingDeployment)
			? az.EmbeddingDeployment!
			: string.Empty;
	}
}

/// <summary>Outcome of a source-pool build.</summary>
public sealed record WikiSourcePoolResult(
	IReadOnlyList<WikiMaintenanceSourceChunk> Chunks,
	string EmbeddingDeployment);
