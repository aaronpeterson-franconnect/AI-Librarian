using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Rls;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres <see cref="IWikiPageReader"/>. Pushes the system admin
/// RLS context so the read works regardless of the caller's role —
/// the maintainer is a system actor and needs to see the lock flag
/// even when running under a service-account scope.
/// </summary>
public sealed class PostgresWikiPageReader : IWikiPageReader
{
	private readonly NpgsqlDataSource _dataSource;

	/// <summary>Creates the reader.</summary>
	public PostgresWikiPageReader(NpgsqlDataSource dataSource)
	{
		_dataSource = dataSource;
	}

	/// <inheritdoc />
	public async Task<bool> IsLockedAsync(Guid pageId, CancellationToken cancellationToken = default)
	{
		if (pageId == Guid.Empty)
		{
			return false;
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = "SELECT locked FROM wiki_pages WHERE id = @id";
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("id", pageId);
		var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		return result is bool b && b;
	}

	/// <inheritdoc />
	public async Task<IReadOnlySet<string>> ListSlugsAsync(
		Guid departmentId,
		CancellationToken cancellationToken = default)
	{
		if (departmentId == Guid.Empty)
		{
			return new HashSet<string>(StringComparer.Ordinal);
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = "SELECT slug FROM wiki_pages WHERE department_id = @d";
		var slugs = new HashSet<string>(StringComparer.Ordinal);
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("d", departmentId);
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				slugs.Add(reader.GetString(0));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return slugs;
	}
}
