using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed <see cref="ISourceTypeBackfiller"/>. One transaction
/// per call: SELECT a bounded set of NULL-source-type rows, run the
/// classifier on each, batched UPDATE, COMMIT. Uses
/// <c>FOR UPDATE SKIP LOCKED</c> so concurrent admins running the
/// backfill don't trample each other (each picks a different bucket
/// of rows).
///
/// <para>Runs in system admin RLS context — soft-deleted rows still
/// get classified (their <c>source_type</c> may matter when they're
/// restored or queried via audit paths that bypass RLS).</para>
/// </summary>
public sealed class PostgresSourceTypeBackfiller : ISourceTypeBackfiller
{
	private const int MaxBatchSize = 500;

	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresSourceTypeBackfiller> _logger;

	/// <summary>Creates the backfiller.</summary>
	public PostgresSourceTypeBackfiller(
		NpgsqlDataSource dataSource,
		ILogger<PostgresSourceTypeBackfiller> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<SourceTypeBackfillOutcome> BackfillBatchAsync(
		int batchSize,
		CancellationToken cancellationToken = default)
	{
		var safeBatch = Math.Clamp(batchSize, 1, MaxBatchSize);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Step 1: pick a bounded set of unclassified rows. SKIP LOCKED
		// means parallel callers naturally partition; the WHERE clause
		// filters out rows another transaction is already updating.
		var candidates = new List<BackfillRow>(capacity: safeBatch);
		const string selectSql = """
			SELECT id, content_type, title
			FROM sources
			WHERE source_type IS NULL
			ORDER BY created_at ASC
			LIMIT @batch
			FOR UPDATE SKIP LOCKED
			""";
		await using (var selectCmd = new NpgsqlCommand(selectSql, conn, tx))
		{
			selectCmd.Parameters.AddWithValue("batch", safeBatch);
			await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				candidates.Add(new BackfillRow(
					Id: reader.GetGuid(0),
					ContentType: reader.IsDBNull(1) ? null : reader.GetString(1),
					Title: reader.IsDBNull(2) ? null : reader.GetString(2)));
			}
		}

		// Step 2: classify each + UPDATE. One UPDATE per row keeps the
		// SQL simple; for the batch sizes we expect (≤500) the
		// overhead is negligible compared to the round-trip per call.
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		const string updateSql = """
			UPDATE sources
			SET source_type = @source_type, updated_at = now()
			WHERE id = @id AND source_type IS NULL
			""";
		foreach (var row in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var sourceType = SourceTypeClassifier.Classify(
				contentType: row.ContentType,
				fileName: null,
				title: row.Title);
			await using var updateCmd = new NpgsqlCommand(updateSql, conn, tx);
			updateCmd.Parameters.AddWithValue("source_type", sourceType);
			updateCmd.Parameters.AddWithValue("id", row.Id);
			var rows = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (rows == 1)
			{
				counts[sourceType] = counts.TryGetValue(sourceType, out var c) ? c + 1 : 1;
			}
		}

		// Step 3: remaining count. Cheap COUNT(*) on the partial
		// index ix_sources_source_type WHERE NOT-null would be even
		// better, but the predicate here is the inverse so we fall
		// back to a regular count. Operators only need an order-of-
		// magnitude estimate to decide whether to keep calling.
		long remaining;
		await using (var countCmd = new NpgsqlCommand(
			"SELECT count(*) FROM sources WHERE source_type IS NULL", conn, tx))
		{
			remaining = (long)(await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		var classifiedThisCall = counts.Values.Sum();
		_logger.LogInformation(
			"SourceType backfill batch: classified={Classified} remaining={Remaining}",
			classifiedThisCall, remaining);

		return new SourceTypeBackfillOutcome(
			ClassifiedThisCall: classifiedThisCall,
			RemainingUnclassified: remaining,
			ClassificationCounts: counts);
	}

	private sealed record BackfillRow(Guid Id, string? ContentType, string? Title);
}

/// <summary>Dev-without-Postgres fallback.</summary>
internal sealed class NullSourceTypeBackfiller : ISourceTypeBackfiller
{
	public Task<SourceTypeBackfillOutcome> BackfillBatchAsync(int batchSize, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Source-type backfill requires ConnectionStrings:Postgres to be configured.");
}
