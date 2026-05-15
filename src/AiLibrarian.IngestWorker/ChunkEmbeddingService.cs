using AiLibrarian.Domain.Ingest;
using AiLibrarian.Domain.Skills;

using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;

using AiLibrarian.LlmGateway.Abstractions;

using Microsoft.Extensions.Options;

namespace AiLibrarian.IngestWorker;

/// <summary>Batch-embeds chunk bodies and writes vectors to <c>source_chunks</c> under RLS.</summary>
internal sealed class ChunkEmbeddingService
{
	private readonly IEmbeddingProvider _embeddingProvider;
	private readonly ISourceChunkPersistence _chunkPersistence;
	private readonly IOptions<IngestWorkerOptions> _options;
	private readonly ILogger<ChunkEmbeddingService> _logger;

	public ChunkEmbeddingService(
		IEmbeddingProvider embeddingProvider,
		ISourceChunkPersistence chunkPersistence,
		IOptions<IngestWorkerOptions> options,
		ILogger<ChunkEmbeddingService> logger)
	{
		_embeddingProvider = embeddingProvider;
		_chunkPersistence = chunkPersistence;
		_options = options;
		_logger = logger;
	}

	public async Task TryEmbedAsync(
		IngestJobMessage job,
		RlsSessionContext rls,
		SkillResult result,
		CancellationToken cancellationToken)
	{
		var opts = _options.Value;
		if (!opts.Processing.GenerateEmbeddings)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(opts.Database.ConnectionString))
		{
			_logger.LogWarning("GenerateEmbeddings is enabled but IngestWorker:Database:ConnectionString is empty; skipping embeddings.");
			return;
		}

		if (job.SourceId is not { } sourceId)
		{
			return;
		}

		if (result.Chunks.Count == 0)
		{
			return;
		}

		var emb = opts.Embeddings;
		if (string.IsNullOrWhiteSpace(emb.ModelDeploymentName))
		{
			throw new InvalidOperationException(
				"IngestWorker:Embeddings:ModelDeploymentName is required when GenerateEmbeddings is true.");
		}

		var texts = result.Chunks.Select(c => c.ContentMarkdown).ToList();
		var correlation = ParseCorrelationGuid(job.CorrelationId);

		var vectors = await _embeddingProvider
			.EmbedAsync(emb.ModelDeploymentName, texts, correlation, cancellationToken)
			.ConfigureAwait(false);

		if (vectors.Count != result.Chunks.Count)
		{
			throw new InvalidOperationException(
				$"Embedding provider returned {vectors.Count} vectors for {result.Chunks.Count} chunks.");
		}

		var expected = emb.ExpectedDimensions;
		for (var i = 0; i < vectors.Count; i++)
		{
			if (vectors[i].Length != expected)
			{
				throw new InvalidOperationException(
					$"Embedding vector length {vectors[i].Length} does not match ExpectedDimensions={expected}.");
			}
		}

		var pairs = new List<(int OrderIndex, ReadOnlyMemory<float> Embedding)>(result.Chunks.Count);
		for (var i = 0; i < result.Chunks.Count; i++)
		{
			pairs.Add((result.Chunks[i].OrderIndex, vectors[i]));
		}

		await _chunkPersistence
			.UpdateEmbeddingsForSourceAsync(
				rls,
				sourceId,
				emb.ModelDeploymentName,
				pairs,
				cancellationToken)
			.ConfigureAwait(false);
	}

	private static Guid ParseCorrelationGuid(string? correlationId)
	{
		if (!string.IsNullOrWhiteSpace(correlationId) && Guid.TryParse(correlationId, out var g))
		{
			return g;
		}

		return Guid.NewGuid();
	}
}
