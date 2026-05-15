using System.Text.Json;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres <see cref="IWikiProposalReader"/>. Runs in the caller's
/// RLS context — the
/// <c>p_wiki_proposed_revisions_read</c> policy lets Admin see
/// everything and Reviewer/Librarian see proposals on pages in their
/// departments. Other roles see nothing.
///
/// <para>The caller is responsible for pushing the RLS context onto
/// a connection before calling here. That's the standard pattern for
/// read-side repositories that participate in route-handler scopes.</para>
/// </summary>
public sealed class PostgresWikiProposalReader : IWikiProposalReader
{
	private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresWikiProposalReader> _logger;

	/// <summary>
	/// Optional RLS session context. When non-null, the reader pushes
	/// it onto each connection it opens. When null, callers are
	/// expected to push their own context upstream (e.g. via a
	/// shared connection abstraction in future slices); for the
	/// current admin endpoints we always pass the system context.
	/// </summary>
	public RlsSessionContext? SessionContext { get; init; }

	/// <summary>Creates the reader.</summary>
	public PostgresWikiProposalReader(
		NpgsqlDataSource dataSource,
		ILogger<PostgresWikiProposalReader> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<WikiProposedRevision?> GetAsync(Guid proposalId, CancellationToken cancellationToken = default)
	{
		if (proposalId == Guid.Empty)
		{
			return null;
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, SessionContext ?? RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = $"""
			SELECT {SelectColumns}
			FROM wiki_proposed_revisions
			WHERE id = @id
			""";

		WikiProposedRevision? result = null;
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("id", proposalId);
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				result = Map(reader);
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return result;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<WikiProposedRevision>> ListAsync(
		ProposalState? state,
		int limit,
		CancellationToken cancellationToken = default)
	{
		var safeLimit = Math.Clamp(limit, 1, 500);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, SessionContext ?? RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		var sql = state is null
			? $"SELECT {SelectColumns} FROM wiki_proposed_revisions ORDER BY authored_at DESC LIMIT @limit"
			: $"SELECT {SelectColumns} FROM wiki_proposed_revisions WHERE state = @state ORDER BY authored_at DESC LIMIT @limit";

		var results = new List<WikiProposedRevision>();
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("limit", safeLimit);
		if (state is { } s)
		{
			cmd.Parameters.AddWithValue("state", s.ToCode());
		}

		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				results.Add(Map(reader));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return results;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<WikiProposedRevision>> ListDecidedAsync(
		Guid? decidedBy,
		DateTimeOffset? since,
		int limit,
		CancellationToken cancellationToken = default)
	{
		var safeLimit = Math.Clamp(limit, 1, 500);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, SessionContext ?? RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// state != 'pending' is the always-on filter: a "decisions" list
		// must only return proposals that actually carry a decision.
		// decided_by / decided_at filters are optional and apply only
		// when supplied.
		var sql = $$"""
			SELECT {{SelectColumns}}
			FROM wiki_proposed_revisions
			WHERE state <> 'pending'
			  AND (@decidedBy IS NULL OR decided_by = @decidedBy)
			  AND (@since IS NULL OR decided_at >= @since)
			ORDER BY decided_at DESC NULLS LAST
			LIMIT @limit
			""";

		var results = new List<WikiProposedRevision>();
		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("limit", safeLimit);
		cmd.Parameters.AddWithValue("decidedBy", (object?)decidedBy ?? DBNull.Value);
		cmd.Parameters.AddWithValue("since", (object?)since ?? DBNull.Value);

		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				results.Add(Map(reader));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return results;
	}

	private const string SelectColumns = """
		id, page_id, min_classification, persona_id, proposed_revision_number,
		authored_by, authored_at, expires_at, body_markdown, proposed_payload,
		state, decided_by, decided_at, decision_reason
		""";

	private static WikiProposedRevision Map(NpgsqlDataReader reader)
	{
		var classification = Enum.TryParse<Classification>(reader.GetString(2), ignoreCase: false, out var c)
			? c
			: Classification.Internal;

		var payloadJson = reader.GetString(9);
		var payload = JsonSerializer.Deserialize<WikiProposalPayload>(payloadJson, PayloadJsonOptions)
			?? new WikiProposalPayload(Array.Empty<WikiClaimDraft>());

		return new WikiProposedRevision(
			Id: reader.GetGuid(0),
			PageId: reader.GetGuid(1),
			MinClassification: classification,
			PersonaId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
			ProposedRevisionNumber: reader.GetInt32(4),
			AuthoredBy: reader.GetGuid(5),
			AuthoredAt: reader.GetFieldValue<DateTimeOffset>(6),
			ExpiresAt: reader.GetFieldValue<DateTimeOffset>(7),
			BodyMarkdown: reader.GetString(8),
			Payload: payload,
			State: ProposalStateCodes.Parse(reader.GetString(10)),
			DecidedBy: reader.IsDBNull(11) ? null : reader.GetGuid(11),
			DecidedAt: reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
			DecisionReason: reader.IsDBNull(13) ? null : reader.GetString(13));
	}
}
