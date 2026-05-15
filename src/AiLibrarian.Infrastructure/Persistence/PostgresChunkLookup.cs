using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed <see cref="IChunkLookup"/>. Reads <c>source_chunks</c>
/// joined to <c>sources</c> so each <see cref="ChunkRef"/> carries the
/// source's classification + the soft-delete signal. RLS applies — the
/// caller only sees chunks they can read; missing-from-result is the
/// citation validator's rule-2 signal regardless of whether the chunk
/// was hard-deleted, soft-deleted-via-source, or just RLS-hidden.
///
/// <para>Soft-deleted chunks: a chunk is considered soft-deleted when
/// its parent <c>sources</c> row has a non-null
/// <c>soft_deleted_at</c>. The chunk row itself doesn't carry a
/// soft-delete column — the source-level flag cascades. The lookup
/// reports both states so the dangling-citation detector can pivot.</para>
/// </summary>
public sealed class PostgresChunkLookup : IChunkLookup
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresChunkLookup> _logger;

	/// <summary>Creates the lookup.</summary>
	public PostgresChunkLookup(
		NpgsqlDataSource dataSource,
		ILogger<PostgresChunkLookup> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<Guid, ChunkRef>> ResolveAsync(
		IReadOnlyCollection<Guid> chunkIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(chunkIds);

		if (chunkIds.Count == 0)
		{
			return new Dictionary<Guid, ChunkRef>();
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// The validator runs in the caller's RLS context; this lookup
		// is invoked from inside the same scope. But the caller's RLS
		// context isn't threaded here -- typical callers (the wiki
		// maintainer, the dangling detector) run as system. Push the
		// system context so the lookup sees every chunk regardless of
		// caller scope. The validator's rule 4 enforces classification
		// up-flow at the per-claim level; the lookup is intentionally
		// liberal about what it returns.
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT
				sc.id,
				sc.source_id,
				s.classification,
				length(sc.content_markdown) AS content_length,
				(s.soft_deleted_at IS NOT NULL) AS is_soft_deleted
			FROM source_chunks sc
			INNER JOIN sources s ON s.id = sc.source_id
			WHERE sc.id = ANY (@ids::uuid[])
			""";

		var result = new Dictionary<Guid, ChunkRef>(capacity: chunkIds.Count);
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("ids", chunkIds.ToArray());

		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var id = reader.GetGuid(0);
				var sourceId = reader.GetGuid(1);
				var classificationRaw = reader.GetString(2);
				var classification = Enum.TryParse<Classification>(classificationRaw, ignoreCase: true, out var cls)
					? cls
					: Classification.Internal;
				var contentLength = reader.GetInt32(3);
				var isSoftDeleted = reader.GetBoolean(4);

				result[id] = new ChunkRef(
					Id: id,
					SourceId: sourceId,
					Classification: classification,
					ContentLength: contentLength,
					IsSoftDeleted: isSoftDeleted);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogDebug("PostgresChunkLookup resolved {Hit}/{Asked} chunks.", result.Count, chunkIds.Count);
		return result;
	}
}
