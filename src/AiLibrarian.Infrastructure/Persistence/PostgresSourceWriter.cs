using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed <see cref="ISourceWriter"/>. Uses the same
/// transaction + RLS-pushdown pattern as
/// <see cref="PostgresSourceRepository"/>; on Phase 1 we auto-approve
/// contributor uploads (<c>approved_by</c> = the contributor,
/// <c>approved_at</c> = <c>now()</c>) so retrieval can use the row
/// immediately. The Phase 2 approval queue ADR will replace this with
/// a per-department policy decision.
/// </summary>
public sealed class PostgresSourceWriter : ISourceWriter
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresSourceWriter> _logger;

	/// <summary>Creates the writer.</summary>
	public PostgresSourceWriter(
		NpgsqlDataSource dataSource,
		ILogger<PostgresSourceWriter> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<Guid> CreateAsync(
		RlsSessionContext context,
		SourceSubmission submission,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(submission);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, context, cancellationToken).ConfigureAwait(false);

		// Classify the source at INSERT time so persona reranking
		// (ADR 0015 sourceTypeWeights) has a value to weight on from
		// the moment the row is live. The classifier is a pure
		// function; null input → "document" fallback so the column
		// stays non-null on every fresh insert. Pre-existing rows
		// remain NULL until the backfill endpoint sweeps them.
		var sourceType = SourceTypeClassifier.Classify(
			contentType: submission.ContentType,
			fileName: null,
			title: submission.Title);

		const string sql = """
			INSERT INTO sources (
				department_id, classification, title, content_type, uri,
				contributed_by, approved_by, approved_at, source_type
			) VALUES (
				@department_id, @classification, @title, @content_type, @uri,
				@contributed_by, @contributed_by, now(), @source_type
			)
			RETURNING id
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("department_id", submission.DepartmentId);
		cmd.Parameters.AddWithValue("classification", submission.Classification.ToString());
		cmd.Parameters.AddWithValue("title", submission.Title);
		cmd.Parameters.AddWithValue("content_type", submission.ContentType);
		cmd.Parameters.AddWithValue("uri", (object?)submission.Uri ?? DBNull.Value);
		cmd.Parameters.AddWithValue("contributed_by", submission.ContributedBy);
		cmd.Parameters.AddWithValue("source_type", sourceType);

		try
		{
			var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (result is not Guid id)
			{
				throw new InvalidOperationException("INSERT did not return an identifier.");
			}

			await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogInformation(
				"source created id={SourceId} dept={DeptId} classification={Classification} contributor={ContributorId}",
				id, submission.DepartmentId, submission.Classification, submission.ContributedBy);
			return id;
		}
		catch (PostgresException ex) when (ex.SqlState == "42501")
		{
			// PostgreSQL: insufficient_privilege — RLS write predicate denied the row.
			throw new UnauthorizedSourceWriteException(
				$"Source insert blocked by RLS write predicate (department={submission.DepartmentId}, classification={submission.Classification}). Caller needs Contributor+ on the target department.",
				ex);
		}
	}

	/// <inheritdoc />
	public async Task<bool> UpdateChecksumAndSizeAsync(
		RlsSessionContext context,
		Guid sourceId,
		string checksumSha256,
		long sizeBytes,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentException.ThrowIfNullOrWhiteSpace(checksumSha256);
		if (sizeBytes < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Must be non-negative.");
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, context, cancellationToken).ConfigureAwait(false);

		const string sql = """
			UPDATE sources
			SET checksum_sha256 = @checksum,
				size_bytes      = @size_bytes,
				updated_at      = now()
			WHERE id = @id
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("id", sourceId);
		cmd.Parameters.AddWithValue("checksum", checksumSha256);
		cmd.Parameters.AddWithValue("size_bytes", sizeBytes);

		var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (rows == 0)
		{
			_logger.LogWarning(
				"checksum/size update touched 0 rows source={SourceId} (missing or RLS-hidden)",
				sourceId);
			return false;
		}

		_logger.LogDebug(
			"source post-ingest update id={SourceId} sha256={Sha256Prefix}.. size={Size}",
			sourceId, checksumSha256[..Math.Min(8, checksumSha256.Length)], sizeBytes);
		return true;
	}
}
