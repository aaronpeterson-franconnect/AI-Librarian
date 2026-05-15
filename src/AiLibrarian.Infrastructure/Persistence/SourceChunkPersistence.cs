using AiLibrarian.Domain.Skills;

using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;
using NpgsqlTypes;

using Pgvector;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>Deletes existing chunks for the source and inserts the new ordered set under RLS.</summary>
public sealed class SourceChunkPersistence : ISourceChunkPersistence
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<SourceChunkPersistence> _logger;

	public SourceChunkPersistence(NpgsqlDataSource dataSource, ILogger<SourceChunkPersistence> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task ReplaceChunksForSourceAsync(
		RlsSessionContext sessionContext,
		Guid sourceId,
		IReadOnlyList<Chunk> chunks,
		CancellationToken cancellationToken)
	{
		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await RlsSessionPusher.PushAsync(conn, sessionContext, cancellationToken).ConfigureAwait(false);

		await using (var delete = new NpgsqlCommand(
			             """
			             DELETE FROM source_chunks WHERE source_id = @source_id
			             """,
			             conn,
			             tx))
		{
			delete.Parameters.AddWithValue("source_id", sourceId);
			var deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogDebug("source_chunks delete for source={SourceId} removed={Count}", sourceId, deleted);
		}

		foreach (var chunk in chunks)
		{
			await using var insert = new NpgsqlCommand(
				"""
				INSERT INTO source_chunks (source_id, order_index, content_markdown, span_anchor)
				VALUES (@source_id, @order_index, @content_markdown, @span_anchor)
				""",
				conn,
				tx);

			insert.Parameters.AddWithValue("source_id", sourceId);
			insert.Parameters.AddWithValue("order_index", chunk.OrderIndex);
			insert.Parameters.AddWithValue("content_markdown", chunk.ContentMarkdown);
			var spanJson = chunk.SpanAnchor.ToJsonString();
			var spanParam = new NpgsqlParameter("span_anchor", NpgsqlDbType.Jsonb)
			{
				Value = spanJson,
			};
			insert.Parameters.Add(spanParam);

			await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		_logger.LogInformation(
			"Persisted {ChunkCount} chunk(s) for source={SourceId}",
			chunks.Count,
			sourceId);
	}

	/// <inheritdoc />
	public async Task UpdateEmbeddingsForSourceAsync(
		RlsSessionContext sessionContext,
		Guid sourceId,
		string embeddingModel,
		IReadOnlyList<(int OrderIndex, ReadOnlyMemory<float> Embedding)> vectors,
		CancellationToken cancellationToken)
	{
		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await RlsSessionPusher.PushAsync(conn, sessionContext, cancellationToken).ConfigureAwait(false);

		foreach (var (orderIndex, embedding) in vectors)
		{
			await using var cmd = new NpgsqlCommand(
				"""
				UPDATE source_chunks
				SET embedding = @embedding,
				    embedding_model = @embedding_model,
				    embedded_at = now()
				WHERE source_id = @source_id AND order_index = @order_index
				""",
				conn,
				tx);

			cmd.Parameters.AddWithValue("source_id", sourceId);
			cmd.Parameters.AddWithValue("order_index", orderIndex);
			cmd.Parameters.AddWithValue("embedding_model", embeddingModel);
			cmd.Parameters.AddWithValue("embedding", new Vector(embedding.ToArray()));

			var updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (updated != 1)
			{
				_logger.LogWarning(
					"Embedding UPDATE expected 1 row for source={SourceId} order={OrderIndex}, got {Rows}",
					sourceId,
					orderIndex,
					updated);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		_logger.LogInformation(
			"Updated embeddings for source={SourceId} rows={Count} model={Model}",
			sourceId,
			vectors.Count,
			embeddingModel);
	}
}
