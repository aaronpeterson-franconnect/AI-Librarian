using AiLibrarian.Domain;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres-backed <see cref="IDepartmentRepository"/>. Same
/// transaction + RLS pushdown pattern as
/// <see cref="PostgresSourceRepository"/>.
/// </summary>
public sealed class PostgresDepartmentRepository : IDepartmentRepository
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresDepartmentRepository> _logger;

	/// <summary>Creates the repository.</summary>
	public PostgresDepartmentRepository(
		NpgsqlDataSource dataSource,
		ILogger<PostgresDepartmentRepository> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<Department>> ListActiveAsync(
		RlsSessionContext context,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, context, cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT id, name, display_name, deactivated_at
			FROM departments
			WHERE deactivated_at IS NULL
			ORDER BY display_name
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);

		// Scoped reader: Npgsql forbids running another command on the
		// same connection while a data reader is open. tx.CommitAsync
		// counts as a command and throws NpgsqlOperationInProgressException
		// if the reader is still alive. Dispose explicitly before commit.
		var results = new List<Department>();
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				results.Add(new Department(
					Id: reader.GetGuid(0),
					Name: reader.GetString(1),
					DisplayName: reader.GetString(2),
					DeactivatedAt: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		_logger.LogDebug("listed {Count} active departments", results.Count);
		return results;
	}
}
