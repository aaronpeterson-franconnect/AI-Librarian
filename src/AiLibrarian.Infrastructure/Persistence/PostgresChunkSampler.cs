using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres <see cref="IChunkSampler"/>. Runs in system admin RLS
/// context — the candidate-discovery flow is a librarian-initiated
/// operation on a whole department; the API handler is what enforces
/// the operator's permission to discover within that department.
///
/// <para>Uses <c>ORDER BY random() LIMIT @n</c>. For Phase 2 corpus
/// sizes (tens of thousands of chunks per department) this is fine
/// and simpler than <c>TABLESAMPLE</c>, which doesn't compose with
/// the join to <c>sources</c> needed to filter soft-deleted sources
/// and propagate classification.</para>
/// </summary>
public sealed class PostgresChunkSampler : IChunkSampler
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresChunkSampler> _logger;

	/// <summary>Creates the sampler.</summary>
	public PostgresChunkSampler(
		NpgsqlDataSource dataSource,
		ILogger<PostgresChunkSampler> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<SampledChunk>> SampleAsync(
		Guid departmentId,
		int limit,
		int maxCharsPerChunk = 4096,
		CancellationToken cancellationToken = default)
	{
		if (departmentId == Guid.Empty)
		{
			throw new ArgumentException("DepartmentId is required.", nameof(departmentId));
		}

		// Clamp limits so a runaway client can't drag the database.
		var safeLimit = Math.Clamp(limit, 1, 500);
		var cap = Math.Clamp(maxCharsPerChunk, 100, 64 * 1024);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT sc.id, left(sc.content_markdown, @cap), s.classification
			FROM source_chunks sc
			INNER JOIN sources s ON s.id = sc.source_id
			WHERE s.department_id = @dept
			  AND s.soft_deleted_at IS NULL
			ORDER BY random()
			LIMIT @limit
			""";

		var results = new List<SampledChunk>(capacity: safeLimit);
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("dept", departmentId);
		cmd.Parameters.AddWithValue("limit", safeLimit);
		cmd.Parameters.AddWithValue("cap", cap);

		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var classText = reader.GetString(2);
				var classification = Enum.TryParse<Classification>(classText, ignoreCase: false, out var c)
					? c
					: Classification.Internal;
				results.Add(new SampledChunk(
					ChunkId: reader.GetGuid(0),
					ContentMarkdown: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
					Classification: classification));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogDebug(
			"PostgresChunkSampler dept={Dept} returned={Count}/{Asked} cap={Cap}.",
			departmentId, results.Count, safeLimit, cap);

		return results;
	}
}
