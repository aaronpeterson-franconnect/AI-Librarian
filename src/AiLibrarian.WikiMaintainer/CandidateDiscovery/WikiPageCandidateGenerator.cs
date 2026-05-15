using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.LlmGateway.Abstractions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.WikiMaintainer.CandidateDiscovery;

/// <summary>
/// Cluster-based wiki-page candidate generator. Pipeline:
/// <list type="number">
///   <item>Sample N chunks for the target department via
///         <see cref="IChunkSampler"/>.</item>
///   <item>Embed every chunk in one batched call via
///         <see cref="IEmbeddingProvider"/>.</item>
///   <item>K-means cluster the embeddings (k auto-derived from
///         <c>maxCandidates</c> and corpus size).</item>
///   <item>For each cluster, take the N representatives nearest the
///         centroid and ask the LLM to propose
///         <c>{title, slug, summary}</c> via
///         <see cref="IChatProvider"/>.</item>
///   <item>Dedupe LLM-proposed slugs against existing
///         <c>wiki_pages.slug</c> for the department (the operator
///         doesn't want to see pages we already have).</item>
///   <item>Return candidates ordered by cluster size (largest first).</item>
/// </list>
///
/// <para>Cost shape per discovery call: 1 batched embedding call +
/// up to <c>maxCandidates</c> small chat calls. The chat prompt sees
/// only the cluster representatives (default 3 chunks per cluster),
/// not the whole sample.</para>
/// </summary>
public sealed class WikiPageCandidateGenerator : IWikiPageCandidateGenerator
{
	private static readonly JsonSerializerOptions NameJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	private readonly IChunkSampler _sampler;
	private readonly IEmbeddingProvider _embeddings;
	private readonly IChatProvider _chat;
	private readonly IWikiPageReader _pageReader;
	private readonly IOptions<WikiPageCandidateGeneratorOptions> _options;
	private readonly ILogger<WikiPageCandidateGenerator> _logger;

	/// <summary>Creates the generator.</summary>
	public WikiPageCandidateGenerator(
		IChunkSampler sampler,
		IEmbeddingProvider embeddings,
		IChatProvider chat,
		IWikiPageReader pageReader,
		IOptions<WikiPageCandidateGeneratorOptions> options,
		ILogger<WikiPageCandidateGenerator> logger)
	{
		_sampler = sampler;
		_embeddings = embeddings;
		_chat = chat;
		_pageReader = pageReader;
		_options = options;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<WikiPageCandidateBatch> DiscoverAsync(
		Guid departmentId,
		int sampleSize,
		int maxCandidates,
		Guid correlationId,
		CancellationToken cancellationToken = default)
	{
		if (departmentId == Guid.Empty)
		{
			throw new ArgumentException("DepartmentId is required.", nameof(departmentId));
		}
		var opts = _options.Value;
		var n = Math.Clamp(sampleSize, 5, 500);
		var maxK = Math.Clamp(maxCandidates, 1, 20);

		// Step 1: sample.
		var sample = await _sampler
			.SampleAsync(departmentId, n, opts.MaxCharsPerChunk, cancellationToken)
			.ConfigureAwait(false);

		if (sample.Count == 0)
		{
			_logger.LogInformation(
				"Candidate discovery dept={Dept} found no chunks to sample.", departmentId);
			return new WikiPageCandidateBatch(
				Array.Empty<WikiPageCandidate>(),
				SampledChunkCount: 0,
				EmbeddingDeployment: opts.EmbeddingDeployment);
		}

		// Step 2: embed. Single batched call -- the embedding provider
		// is expected to handle the batch size internally.
		var embedTexts = sample.Select(c => c.ContentMarkdown).ToArray();
		var vectors = await _embeddings
			.EmbedAsync(opts.EmbeddingDeployment, embedTexts, correlationId, cancellationToken)
			.ConfigureAwait(false);
		if (vectors.Count != sample.Count)
		{
			throw new InvalidOperationException(
				$"Embedding provider returned {vectors.Count} vectors for {sample.Count} chunks.");
		}
		var rawVectors = vectors.Select(m => m.ToArray()).ToList();

		// Step 3: cluster. k = min(maxCandidates, sample/2) so each
		// cluster has on average >= 2 chunks; small samples won't be
		// over-split.
		var targetK = Math.Min(maxK, Math.Max(1, sample.Count / 2));
		var assignments = KMeans.Cluster(rawVectors, targetK, maxIterations: 30);

		// Step 4: group chunks by cluster; sort clusters by size desc.
		var clusters = new Dictionary<int, List<int>>();
		for (var i = 0; i < assignments.Length; i++)
		{
			if (!clusters.TryGetValue(assignments[i], out var list))
			{
				list = new List<int>();
				clusters[assignments[i]] = list;
			}
			list.Add(i);
		}
		var orderedClusters = clusters.Values
			.OrderByDescending(list => list.Count)
			.ToList();

		// Step 5: name each cluster via the LLM. We use the chunks
		// closest to the centroid as the "representatives" so the
		// prompt sees the cluster's most-typical content. Existing-slug
		// dedupe applies after naming.
		var existingSlugs = await _pageReader
			.ListSlugsAsync(departmentId, cancellationToken)
			.ConfigureAwait(false);

		var candidates = new List<WikiPageCandidate>();
		foreach (var cluster in orderedClusters)
		{
			if (candidates.Count >= maxK)
			{
				break;
			}
			var reps = PickRepresentatives(cluster, rawVectors, opts.RepresentativesPerCluster);

			NameResponse? named;
			try
			{
				named = await NameClusterAsync(reps.Select(i => sample[i]).ToArray(), correlationId, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogWarning(ex,
					"Cluster naming failed for cluster of size {Size}; skipping.", cluster.Count);
				continue;
			}

			if (named is null
				|| string.IsNullOrWhiteSpace(named.Title)
				|| string.IsNullOrWhiteSpace(named.Slug))
			{
				continue;
			}

			// Validate the LLM-proposed slug; fall back to deriving one
			// from the title if the LLM produced something the DB check
			// constraint would reject.
			var slug = WikiSlug.IsValid(named.Slug) ? named.Slug : WikiSlug.From(named.Title);
			if (slug is null || !WikiSlug.IsValid(slug))
			{
				continue;
			}

			if (existingSlugs.Contains(slug))
			{
				// Page already exists; skip the candidate.
				continue;
			}

			// Highest classification observed across the cluster sets the
			// recommended facet ceiling.
			var highestClass = cluster
				.Select(i => sample[i].Classification)
				.Aggregate(Classification.Public, (acc, c) => c > acc ? c : acc);

			candidates.Add(new WikiPageCandidate(
				ProposedTitle: named.Title.Trim(),
				ProposedSlug: slug,
				Summary: named.Summary?.Trim() ?? string.Empty,
				HighestClassification: highestClass,
				SupportingChunkIds: reps.Select(i => sample[i].ChunkId).ToArray(),
				ClusterSize: cluster.Count));
		}

		_logger.LogInformation(
			"Candidate discovery dept={Dept} sample={Sample} clusters={Clusters} returned={Returned}",
			departmentId, sample.Count, orderedClusters.Count, candidates.Count);

		return new WikiPageCandidateBatch(
			candidates,
			SampledChunkCount: sample.Count,
			EmbeddingDeployment: opts.EmbeddingDeployment);
	}

	private static int[] PickRepresentatives(List<int> clusterIndexes, List<float[]> vectors, int count)
	{
		if (clusterIndexes.Count <= count)
		{
			return clusterIndexes.ToArray();
		}
		// Build a centroid for the cluster, then rank members by
		// distance to it. This is a lightweight re-computation; for a
		// 100-chunk sample with 5 clusters it's negligible.
		var dim = vectors[0].Length;
		var centroid = new float[dim];
		foreach (var idx in clusterIndexes)
		{
			var v = vectors[idx];
			for (var d = 0; d < dim; d++)
			{
				centroid[d] += v[d];
			}
		}
		for (var d = 0; d < dim; d++)
		{
			centroid[d] /= clusterIndexes.Count;
		}
		return clusterIndexes
			.OrderBy(idx => KMeans.CosineDistance(vectors[idx], centroid))
			.Take(count)
			.ToArray();
	}

	private async Task<NameResponse?> NameClusterAsync(
		SampledChunk[] representatives,
		Guid correlationId,
		CancellationToken cancellationToken)
	{
		var opts = _options.Value;
		var sysPrompt = """
			You name clusters of internal-corpus document chunks for a
			company wiki. Given the chunks below, propose ONE concept
			that summarizes the cluster. Respond with JSON only, no
			prose, matching this exact shape:

			{ "title": "<<=80 chars, title-case>", "slug": "<lowercase-with-hyphens, <= 60 chars>", "summary": "<1 sentence, <=200 chars>" }

			Rules:
			- title is the human-readable page name (not a verb phrase, not a question).
			- slug is URL-safe: ^[a-z0-9][a-z0-9\-]{0,254}$.
			- summary states what the page would cover.
			- If the chunks do not share a coherent concept, return null for all three fields.
			""";

		var userBuilder = new StringBuilder();
		userBuilder.AppendLine("Chunks in this cluster:");
		userBuilder.AppendLine();
		for (var i = 0; i < representatives.Length; i++)
		{
			userBuilder.Append("--- chunk ").Append(i + 1).AppendLine(" ---");
			userBuilder.AppendLine(representatives[i].ContentMarkdown);
			userBuilder.AppendLine();
		}

		var request = new ChatCompletionRequest(
			Model: opts.ChatDeployment,
			Messages: new[]
			{
				new ChatMessage("system", sysPrompt),
				new ChatMessage("user", userBuilder.ToString()),
			},
			MaxTokens: opts.MaxNamingTokens,
			Temperature: opts.NamingTemperature,
			PersonaId: null,
			CorrelationId: correlationId);

		var sb = new StringBuilder();
		await foreach (var chunk in _chat.StreamCompletionAsync(request, cancellationToken).ConfigureAwait(false))
		{
			if (!string.IsNullOrEmpty(chunk.DeltaContent))
			{
				sb.Append(chunk.DeltaContent);
			}
		}

		var raw = sb.ToString();
		// Tolerant JSON extraction: the model may wrap its output in
		// ```json fences. Pull out the first {...} block.
		var json = ExtractJsonObject(raw);
		if (json is null)
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize<NameResponse>(json, NameJsonOptions);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? ExtractJsonObject(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}
		var start = raw.IndexOf('{');
		var end = raw.LastIndexOf('}');
		if (start < 0 || end < start)
		{
			return null;
		}
		return raw.Substring(start, end - start + 1);
	}

	private sealed record NameResponse(
		[property: JsonPropertyName("title")] string? Title,
		[property: JsonPropertyName("slug")] string? Slug,
		[property: JsonPropertyName("summary")] string? Summary);
}

/// <summary>Tuning knobs for <see cref="WikiPageCandidateGenerator"/>.</summary>
public sealed class WikiPageCandidateGeneratorOptions
{
	/// <summary>Configuration section name (<c>WikiCandidateDiscovery</c>).</summary>
	public const string SectionName = "WikiCandidateDiscovery";

	/// <summary>Embedding deployment used to embed sampled chunks.</summary>
	public string EmbeddingDeployment { get; set; } = string.Empty;

	/// <summary>Chat deployment used to name clusters. Default <c>gpt-4o-mini</c>.</summary>
	public string ChatDeployment { get; set; } = "gpt-4o-mini";

	/// <summary>How many cluster representatives to send to the LLM per cluster. Default 3.</summary>
	public int RepresentativesPerCluster { get; set; } = 3;

	/// <summary>Per-chunk content cap when sampling. Default 2048 — smaller than the maintenance cap because we only need topical signal, not full citations.</summary>
	public int MaxCharsPerChunk { get; set; } = 2048;

	/// <summary>Max tokens per naming call. Default 200 — naming is short JSON.</summary>
	public int MaxNamingTokens { get; set; } = 200;

	/// <summary>Naming-call temperature. Default 0.2 for stable proposals.</summary>
	public double NamingTemperature { get; set; } = 0.2;
}
