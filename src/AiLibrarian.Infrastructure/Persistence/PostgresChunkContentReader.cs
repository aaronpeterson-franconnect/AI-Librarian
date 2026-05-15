using AiLibrarian.Domain.Citations;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres <see cref="IChunkContentReader"/>. Runs in the system
/// admin context — the Wiki Maintainer is a system actor and needs
/// to see content for any chunk the operator scoped through the
/// retrieval pool, regardless of any specific caller's role. The
/// upstream RLS-filtered retrieval is the access boundary; this
/// reader is the content fetch.
///
/// <para>Uses Postgres's <c>left(content_markdown, @cap)</c> server-
/// side truncation so we don't ship megabytes per chunk over the
/// wire when the cap is much smaller than the actual content.</para>
/// </summary>
public sealed class PostgresChunkContentReader : IChunkContentReader
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresChunkContentReader> _logger;

	/// <summary>Creates the reader.</summary>
	public PostgresChunkContentReader(
		NpgsqlDataSource dataSource,
		ILogger<PostgresChunkContentReader> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<Guid, string>> ReadContentAsync(
		IReadOnlyCollection<Guid> chunkIds,
		int maxCharsPerChunk = 4096,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(chunkIds);
		if (chunkIds.Count == 0)
		{
			return new Dictionary<Guid, string>();
		}

		var cap = Math.Clamp(maxCharsPerChunk, 100, 64 * 1024);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT sc.id, left(sc.content_markdown, @cap)
			FROM source_chunks sc
			INNER JOIN sources s ON s.id = sc.source_id
			WHERE sc.id = ANY (@ids::uuid[])
			""";

		var result = new Dictionary<Guid, string>(capacity: chunkIds.Count);
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("ids", chunkIds.ToArray());
		cmd.Parameters.AddWithValue("cap", cap);

		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var id = reader.GetGuid(0);
				var content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
				result[id] = content;
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogDebug(
			"PostgresChunkContentReader resolved {Hits}/{Asked} chunks (cap={Cap}).",
			result.Count,
			chunkIds.Count,
			cap);

		return result;
	}
}
