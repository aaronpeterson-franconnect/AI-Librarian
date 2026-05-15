using System.Diagnostics;

using AiLibrarian.Auditing;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using Pgvector;

namespace AiLibrarian.Infrastructure.Retrieval;

/// <summary>Cosine distance via pgvector plus lexical <c>ts_rank_cd</c> on <c>search_document</c>.</summary>
public sealed class HybridChunkSearch : IHybridChunkSearch
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly IOptions<SearchOptions> _options;
	private readonly ILogger<HybridChunkSearch> _logger;

	public HybridChunkSearch(
		NpgsqlDataSource dataSource,
		IOptions<SearchOptions> options,
		ILogger<HybridChunkSearch> logger)
	{
		_dataSource = dataSource;
		_options = options;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<HybridChunkHit>> SearchAsync(
		RlsSessionContext sessionContext,
		string queryText,
		ReadOnlyMemory<float> queryEmbedding,
		HybridSearchRequestOptions options,
		CancellationToken cancellationToken)
	{
		var dim = _options.Value.ExpectedEmbeddingDimensions;
		if (queryEmbedding.Length != dim)
		{
			throw new ArgumentException($"Query embedding length {queryEmbedding.Length} must be {dim}.", nameof(queryEmbedding));
		}

		var vw = Math.Clamp(options.VectorWeight, 0.0, 1.0);
		var tw = 1.0 - vw;

		using var activity = AiLibActivitySource.Search.StartActivity("ailib.search.hybrid", ActivityKind.Internal);
		activity?.SetTag("ailib.search.vector_weight", vw);
		activity?.SetTag("ailib.search.limit", options.Limit);
		if (sessionContext.PersonaId is { } pid)
		{
			activity?.SetTag(AiLibActivitySource.Attributes.PersonaId, pid);
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await RlsSessionPusher.PushAsync(conn, sessionContext, cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT
				sc.id,
				sc.source_id,
				sc.order_index,
				left(sc.content_markdown, 600) AS excerpt,
				(
					CASE WHEN sc.embedding IS NOT NULL
						THEN (1.0::double precision - ((sc.embedding <=> @qv::vector) / 2.0)) * @vw
						ELSE 0.0::double precision
					END
					+
					CASE WHEN t.tsq @@ sc.search_document
						THEN ts_rank_cd(sc.search_document, t.tsq) * @tw
						ELSE 0.0::double precision
					END
				) AS hybrid_score,
				CASE WHEN sc.embedding IS NOT NULL THEN (sc.embedding <=> @qv::vector) ELSE NULL END AS vec_dist,
				CASE WHEN t.tsq @@ sc.search_document THEN ts_rank_cd(sc.search_document, t.tsq) ELSE NULL END AS txt_rank,
				s.classification,
				s.department_id,
				s.created_at,
				s.approved_at,
				s.source_type
			FROM source_chunks sc
			INNER JOIN sources s ON s.id = sc.source_id
			CROSS JOIN LATERAL (SELECT websearch_to_tsquery('english', @ft::text) AS tsq) t
			WHERE s.soft_deleted_at IS NULL
				AND (
					sc.embedding IS NOT NULL
					OR (t.tsq @@ sc.search_document)
				)
			ORDER BY hybrid_score DESC
			LIMIT @take
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("ft", queryText);
		cmd.Parameters.AddWithValue("qv", new Vector(queryEmbedding.ToArray()));
		cmd.Parameters.AddWithValue("vw", vw);
		cmd.Parameters.AddWithValue("tw", tw);
		cmd.Parameters.AddWithValue("take", options.Limit);

		// Scoped reader: Npgsql forbids running another command on the same
		// connection while a data reader is open; tx.CommitAsync counts as
		// a command and throws NpgsqlOperationInProgressException if the
		// reader is still alive at commit time. Dispose explicitly before
		// committing.
		var hits = new List<HybridChunkHit>();
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var chunkId = reader.GetGuid(0);
				var sourceId = reader.GetGuid(1);
				var orderIndex = reader.GetInt32(2);
				var excerpt = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
				var hybridScore = reader.GetDouble(4);
				double? vecDist = reader.IsDBNull(5) ? null : reader.GetDouble(5);
				double? txtRank = reader.IsDBNull(6) ? null : reader.GetDouble(6);
				var classification = Enum.TryParse<AiLibrarian.Domain.Classification>(reader.GetString(7), ignoreCase: true, out var cls)
					? cls
					: AiLibrarian.Domain.Classification.Internal;
				var deptId = reader.IsDBNull(8) ? Guid.Empty : reader.GetGuid(8);
				DateTimeOffset? sourceCreatedAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9);
				DateTimeOffset? sourceApprovedAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10);
				string? sourceType = reader.IsDBNull(11) ? null : reader.GetString(11);

				hits.Add(new HybridChunkHit(
					ChunkId: chunkId,
					SourceId: sourceId,
					OrderIndex: orderIndex,
					Excerpt: excerpt,
					HybridScore: hybridScore,
					CosineDistance: vecDist,
					TextRank: txtRank,
					SourceClassification: classification,
					SourceDepartmentId: deptId,
					SourceCreatedAt: sourceCreatedAt,
					SourceApprovedAt: sourceApprovedAt,
					SourceType: sourceType));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		activity?.SetTag(AiLibActivitySource.Attributes.SearchHitCount, hits.Count);
		_logger.LogDebug("Hybrid search returned {Count} hit(s) limit={Limit}", hits.Count, options.Limit);

		return hits;
	}
}
