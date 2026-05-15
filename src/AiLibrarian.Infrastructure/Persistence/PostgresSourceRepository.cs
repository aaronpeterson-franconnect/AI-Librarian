using AiLibrarian.Domain;
using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed <see cref="ISourceRepository"/>. Mirrors the
/// transaction + RLS-pushdown pattern used by
/// <c>HybridChunkSearch</c>: open a connection, begin a transaction,
/// push the session vars, then run the SELECT.
/// </summary>
public sealed class PostgresSourceRepository : ISourceRepository
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresSourceRepository> _logger;

	/// <summary>Creates the repository.</summary>
	public PostgresSourceRepository(
		NpgsqlDataSource dataSource,
		ILogger<PostgresSourceRepository> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<Source?> GetByIdAsync(
		RlsSessionContext context,
		Guid sourceId,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, context, cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT id, department_id, classification, title, uri, content_type,
				checksum_sha256, size_bytes, contributed_by, approved_by, approved_at,
				soft_deleted_at, created_at, updated_at
			FROM sources
			WHERE id = @id
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("id", sourceId);

		// Scoped reader: Npgsql forbids running another command (including
		// tx.CommitAsync) on the same connection while a data reader is
		// open. The bare `await using var reader = ...` form keeps the
		// reader alive until method exit, which throws
		// NpgsqlOperationInProgressException on the commit below. Wrap
		// the reader so it disposes BEFORE we commit.
		Source? source = null;
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				source = MapSource(reader);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (source is null)
		{
			_logger.LogDebug("source not found or hidden by RLS id={Id}", sourceId);
		}

		return source;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<Source>> ListAsync(
		RlsSessionContext context,
		Guid? departmentId,
		int limit,
		int offset,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		if (limit <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(limit), limit, "Must be positive.");
		}

		if (offset < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(offset), offset, "Must be non-negative.");
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, context, cancellationToken).ConfigureAwait(false);

		var sql = """
			SELECT id, department_id, classification, title, uri, content_type,
				checksum_sha256, size_bytes, contributed_by, approved_by, approved_at,
				soft_deleted_at, created_at, updated_at
			FROM sources
			WHERE soft_deleted_at IS NULL
			""";

		if (departmentId.HasValue)
		{
			sql += " AND department_id = @department_id";
		}

		sql += " ORDER BY created_at DESC LIMIT @take OFFSET @skip";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		if (departmentId.HasValue)
		{
			cmd.Parameters.AddWithValue("department_id", departmentId.Value);
		}

		cmd.Parameters.AddWithValue("take", limit);
		cmd.Parameters.AddWithValue("skip", offset);

		// Scoped reader so it disposes before tx.CommitAsync; see comment
		// in GetByIdAsync for the Npgsql one-command-per-connection rule.
		var results = new List<Source>();
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				results.Add(MapSource(reader));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		_logger.LogDebug(
			"sources list dept={Dept} limit={Limit} offset={Offset} returned={Count}",
			departmentId, limit, offset, results.Count);
		return results;
	}

	/// <inheritdoc />
	public async Task<Source?> FindByChecksumAsync(
		RlsSessionContext context,
		Guid departmentId,
		string checksumSha256,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentException.ThrowIfNullOrWhiteSpace(checksumSha256);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, context, cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT id, department_id, classification, title, uri, content_type,
				checksum_sha256, size_bytes, contributed_by, approved_by, approved_at,
				soft_deleted_at, created_at, updated_at
			FROM sources
			WHERE department_id = @department_id
				AND checksum_sha256 = @checksum
				AND soft_deleted_at IS NULL
			ORDER BY created_at DESC
			LIMIT 1
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("department_id", departmentId);
		cmd.Parameters.AddWithValue("checksum", checksumSha256);

		// Scoped reader so it disposes before tx.CommitAsync; see comment
		// in GetByIdAsync for the Npgsql one-command-per-connection rule.
		Source? source = null;
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				source = MapSource(reader);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return source;
	}

	private static Source MapSource(NpgsqlDataReader r)
	{
		return new Source(
			Id: r.GetGuid(0),
			DepartmentId: r.GetGuid(1),
			Classification: Enum.Parse<Classification>(r.GetString(2)),
			Title: r.GetString(3),
			Uri: r.IsDBNull(4) ? null : r.GetString(4),
			ContentType: r.GetString(5),
			ChecksumSha256: r.IsDBNull(6) ? null : r.GetString(6),
			SizeBytes: r.IsDBNull(7) ? null : r.GetInt64(7),
			ContributedBy: r.GetGuid(8),
			ApprovedBy: r.IsDBNull(9) ? null : r.GetGuid(9),
			ApprovedAt: r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
			SoftDeletedAt: r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
			CreatedAt: r.GetFieldValue<DateTimeOffset>(12),
			UpdatedAt: r.GetFieldValue<DateTimeOffset>(13));
	}
}
