using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres <see cref="IWikiRevisionWriter"/>. Inserts the revision +
/// every claim + every citation in one transaction so a half-written
/// revision can never exist. Uses the system admin RLS context — the
/// wiki write policies are <c>app_is_admin()</c> only until the Phase
/// 2.5 approval queue lands.
///
/// <para>The actual SQL lives in <see cref="WikiRevisionInserter"/>
/// so that <see cref="PostgresWikiProposalWriter"/> can compose the
/// same insert sequence inside its own transaction during the accept
/// path.</para>
/// </summary>
public sealed class PostgresWikiRevisionWriter : IWikiRevisionWriter
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresWikiRevisionWriter> _logger;

	/// <summary>Creates the writer.</summary>
	public PostgresWikiRevisionWriter(
		NpgsqlDataSource dataSource,
		ILogger<PostgresWikiRevisionWriter> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<Guid> CommitAsync(WikiRevisionDraft draft, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(draft);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		var revisionId = await WikiRevisionInserter
			.InsertAsync(conn, tx, draft, cancellationToken)
			.ConfigureAwait(false);

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation(
			"Committed wiki revision {RevisionId} page={PageId} classification={Classification} persona={Persona} revno={Revno} claims={ClaimCount}",
			revisionId,
			draft.PageId,
			draft.MinClassification,
			draft.PersonaId,
			draft.RevisionNumber,
			draft.Claims.Count);

		return revisionId;
	}
}
