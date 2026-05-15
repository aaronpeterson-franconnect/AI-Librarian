using System.Text.Json;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;
using NpgsqlTypes;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Postgres <see cref="IWikiProposalWriter"/>. Phase 2.5 approval
/// queue per ADR 0006. Operates under the system admin RLS context
/// for create / expire (the maintainer + sweep are system actors),
/// and trusts the caller-supplied <c>decidedBy</c> on the decide
/// path so audit + reviewer attribution stay correct.
/// </summary>
public sealed class PostgresWikiProposalWriter : IWikiProposalWriter
{
	private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresWikiProposalWriter> _logger;

	/// <summary>Creates the writer.</summary>
	public PostgresWikiProposalWriter(
		NpgsqlDataSource dataSource,
		ILogger<PostgresWikiProposalWriter> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<Guid> CreateAsync(WikiProposedRevision proposal, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(proposal);

		if (proposal.State != ProposalState.Pending)
		{
			throw new ArgumentException("New proposals must start in Pending state.", nameof(proposal));
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		var payloadJson = JsonSerializer.Serialize(proposal.Payload, PayloadJsonOptions);

		const string sql = """
			INSERT INTO wiki_proposed_revisions (
				page_id, min_classification, persona_id, proposed_revision_number,
				authored_by, expires_at, body_markdown, proposed_payload, state)
			VALUES (
				@page_id, @classification, @persona_id, @revno,
				@authored_by, @expires_at, @body, @payload::jsonb, 'pending')
			RETURNING id
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("page_id", proposal.PageId);
		cmd.Parameters.AddWithValue("classification", proposal.MinClassification.ToString());
		cmd.Parameters.AddWithValue("persona_id", (object?)proposal.PersonaId ?? DBNull.Value);
		cmd.Parameters.AddWithValue("revno", proposal.ProposedRevisionNumber);
		cmd.Parameters.AddWithValue("authored_by", proposal.AuthoredBy);
		cmd.Parameters.AddWithValue("expires_at", proposal.ExpiresAt);
		cmd.Parameters.AddWithValue("body", proposal.BodyMarkdown);
		cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payloadJson });

		var newId = (Guid)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation(
			"Created wiki proposal {ProposalId} page={PageId} facet={Classification} revno={Revno} expires={Expires}",
			newId,
			proposal.PageId,
			proposal.MinClassification,
			proposal.ProposedRevisionNumber,
			proposal.ExpiresAt);

		return newId;
	}

	/// <inheritdoc />
	public async Task<Guid?> DecideAsync(
		Guid proposalId,
		ProposalState decision,
		Guid decidedBy,
		string? reason,
		CancellationToken cancellationToken = default)
	{
		if (decision != ProposalState.Accepted && decision != ProposalState.Rejected)
		{
			throw new ArgumentException(
				"Decision must be Accepted or Rejected. Use ExpirePendingAsync for state=Expired.",
				nameof(decision));
		}

		if (decidedBy == Guid.Empty)
		{
			throw new ArgumentException("decidedBy must be non-empty.", nameof(decidedBy));
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		// System context so the writer can transition the row + (on
		// accept) write the downstream revision rows. The route handler
		// has already enforced "caller is Admin OR Reviewer/Librarian
		// on the page's department" upstream.
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Fetch the proposal first -- need its draft to commit a real
		// revision on accept, and to verify it's still pending.
		var loadSql = """
			SELECT
				page_id, min_classification, persona_id, proposed_revision_number,
				authored_by, body_markdown, proposed_payload, state
			FROM wiki_proposed_revisions
			WHERE id = @id
			FOR UPDATE
			""";

		Guid pageId;
		Classification classification;
		Guid? personaId;
		int revno;
		Guid authoredBy;
		string bodyMarkdown;
		WikiProposalPayload payload;
		ProposalState currentState;

		await using (var loadCmd = new NpgsqlCommand(loadSql, conn, tx))
		{
			loadCmd.Parameters.AddWithValue("id", proposalId);
			await using var reader = await loadCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				throw new InvalidOperationException($"Proposal {proposalId:D} not found (or RLS-hidden).");
			}

			pageId = reader.GetGuid(0);
			classification = Enum.TryParse<Classification>(reader.GetString(1), ignoreCase: false, out var c)
				? c
				: Classification.Internal;
			personaId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
			revno = reader.GetInt32(3);
			authoredBy = reader.GetGuid(4);
			bodyMarkdown = reader.GetString(5);
			var payloadJson = reader.GetString(6);
			payload = JsonSerializer.Deserialize<WikiProposalPayload>(payloadJson, PayloadJsonOptions)
				?? throw new InvalidOperationException($"Proposal {proposalId:D} has an unparseable payload.");
			currentState = ProposalStateCodes.Parse(reader.GetString(7));
		}

		if (currentState != ProposalState.Pending)
		{
			throw new InvalidOperationException(
				$"Proposal {proposalId:D} is in state {currentState}; only pending proposals can be decided.");
		}

		Guid? newRevisionId = null;
		if (decision == ProposalState.Accepted)
		{
			// Commit the real revision inside the SAME transaction so
			// proposal-state and revision-existence move atomically.
			var draft = new WikiRevisionDraft(
				PageId: pageId,
				MinClassification: classification,
				PersonaId: personaId,
				RevisionNumber: revno,
				AuthoredBy: authoredBy,
				BodyMarkdown: bodyMarkdown,
				Claims: payload.Claims);

			newRevisionId = await WikiRevisionInserter
				.InsertAsync(conn, tx, draft, cancellationToken)
				.ConfigureAwait(false);
		}

		const string updateSql = """
			UPDATE wiki_proposed_revisions
			SET state = @state,
			    decided_by = @decided_by,
			    decided_at = now(),
			    decision_reason = @reason
			WHERE id = @id AND state = 'pending'
			""";

		await using (var updateCmd = new NpgsqlCommand(updateSql, conn, tx))
		{
			updateCmd.Parameters.AddWithValue("state", decision.ToCode());
			updateCmd.Parameters.AddWithValue("decided_by", decidedBy);
			updateCmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
			updateCmd.Parameters.AddWithValue("id", proposalId);
			var rows = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (rows == 0)
			{
				throw new InvalidOperationException(
					$"Proposal {proposalId:D} state changed during the decide call (concurrent decision?).");
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation(
			"Decided wiki proposal {ProposalId} -> {Decision} by user={DecidedBy} revision={RevisionId}",
			proposalId,
			decision,
			decidedBy,
			newRevisionId);

		return newRevisionId;
	}

	/// <inheritdoc />
	public async Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
	{
		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			UPDATE wiki_proposed_revisions
			SET state = 'expired',
			    decided_by = @system,
			    decided_at = now(),
			    decision_reason = 'expired without review'
			WHERE state = 'pending' AND expires_at < now()
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("system", AuditConstantsBridge.SystemUserId);
		var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		if (rows > 0)
		{
			_logger.LogInformation("Expired {Count} stale wiki proposal(s).", rows);
		}

		return rows;
	}

	/// <inheritdoc />
	public async Task<BulkRejectOutcome> BulkRejectAsync(
		IReadOnlyCollection<Guid> proposalIds,
		Guid decidedBy,
		string reason,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(proposalIds);
		if (string.IsNullOrWhiteSpace(reason))
		{
			throw new ArgumentException("Reason is required for bulk reject.", nameof(reason));
		}
		if (decidedBy == Guid.Empty)
		{
			throw new ArgumentException("DecidedBy is required.", nameof(decidedBy));
		}

		// Dedupe so the per-id outcome arrays don't carry duplicates.
		var distinct = proposalIds.Distinct().ToArray();
		if (distinct.Length == 0)
		{
			return new BulkRejectOutcome(
				Array.Empty<Guid>(),
				Array.Empty<Guid>(),
				Array.Empty<Guid>());
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		// Step 1: classify each id into rejected / skipped / notfound.
		// UPDATE ... RETURNING gives us the rejected set; a second SELECT
		// over the remaining ids gives us the skipped (state already
		// terminal) vs. not-found split.
		var rejected = new List<Guid>();
		const string updateSql = """
			UPDATE wiki_proposed_revisions
			SET state = 'rejected',
			    decided_by = @decider,
			    decided_at = now(),
			    decision_reason = @reason
			WHERE id = ANY (@ids::uuid[]) AND state = 'pending'
			RETURNING id
			""";
		await using (var cmd = new NpgsqlCommand(updateSql, conn, tx))
		{
			cmd.Parameters.AddWithValue("decider", decidedBy);
			cmd.Parameters.AddWithValue("reason", reason);
			cmd.Parameters.AddWithValue("ids", distinct);
			await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await rdr.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				rejected.Add(rdr.GetGuid(0));
			}
		}

		// Step 2: SELECT the still-existing-but-not-rejected ids to
		// distinguish "skipped" (existed, not pending) from "not found".
		var rejectedSet = rejected.ToHashSet();
		var remaining = distinct.Where(id => !rejectedSet.Contains(id)).ToArray();
		var skipped = new List<Guid>();
		if (remaining.Length > 0)
		{
			const string existsSql = """
				SELECT id FROM wiki_proposed_revisions
				WHERE id = ANY (@ids::uuid[])
				""";
			await using var cmd = new NpgsqlCommand(existsSql, conn, tx);
			cmd.Parameters.AddWithValue("ids", remaining);
			await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await rdr.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				skipped.Add(rdr.GetGuid(0));
			}
		}

		var skippedSet = skipped.ToHashSet();
		var notFound = remaining.Where(id => !skippedSet.Contains(id)).ToList();

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogInformation(
			"BulkReject decider={Decider} rejected={Rejected} skipped={Skipped} notFound={NotFound}",
			decidedBy, rejected.Count, skipped.Count, notFound.Count);

		return new BulkRejectOutcome(rejected, skipped, notFound);
	}
}

/// <summary>
/// Tiny shim so the Infrastructure project can name the system user id
/// without taking a transitive dependency on
/// <c>AiLibrarian.Auditing</c> for one constant. Mirrors
/// <c>AuditConstants.SystemUserId</c>.
/// </summary>
internal static class AuditConstantsBridge
{
	internal static readonly Guid SystemUserId = new("00000000-0000-0000-0000-00000000FFFF");
}
