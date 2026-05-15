using System.Diagnostics;
using System.Text.Json;

using AiLibrarian.Auditing;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;

namespace AiLibrarian.Infrastructure.Auditing;

/// <summary>
/// Postgres-backed <see cref="IAuditWriter"/>. Writes go to the
/// <c>audit_events</c> partitioned parent table from changeset
/// <c>0006-audit-events-1</c>; the partitioning key is
/// <c>occurred_at</c> so a row lands in the correct monthly partition
/// automatically. Append-only is enforced by the trigger from
/// <c>0006-audit-events-3</c> and the RLS policy from
/// <c>0099-rls-audit-1</c>.
///
/// <para>
/// Each write opens a short-lived transaction and pushes a
/// <see cref="RlsSessionContext.System"/> session so the
/// <c>app_is_authenticated()</c> insert predicate is satisfied. The
/// audit row's actor identity comes from <see cref="AuditEvent.ActorUserId"/>
/// (the column), not from the session — the session is purely an RLS
/// gate.
/// </para>
/// </summary>
public sealed class PostgresAuditWriter : IAuditWriter, IAuditQueryService
{
	private readonly NpgsqlDataSource _dataSource;
	private readonly ILogger<PostgresAuditWriter> _logger;

	/// <summary>Creates the writer; <see cref="NpgsqlDataSource"/> comes from DI.</summary>
	public PostgresAuditWriter(
		NpgsqlDataSource dataSource,
		ILogger<PostgresAuditWriter> logger)
	{
		_dataSource = dataSource;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task WriteAsync(
		AuditEvent evt,
		AuditCriticality criticality = AuditCriticality.Critical,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(evt);
		_ = criticality; // Criticality is honored by the breaker decorator; the writer just persists.

		using var activity = AiLibActivitySource.Audit.StartActivity("ailib.audit.write", ActivityKind.Internal);
		activity?.SetTag(AiLibActivitySource.Attributes.AuditEventType, evt.EventType);
		activity?.SetTag(AiLibActivitySource.Attributes.AuditEventSubtype, evt.EventSubtype);
		activity?.SetTag(AiLibActivitySource.Attributes.AuditCriticality, criticality.ToString());
		activity?.SetTag(AiLibActivitySource.Attributes.CorrelationId, evt.CorrelationId);

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			INSERT INTO audit_events (
				id, occurred_at, actor_user_id, actor_role, originated_by, department_id,
				event_type, event_subtype, target_kind, target_id, correlation_id,
				outcome, error_class,
				llm_provider, llm_model, llm_prompt_tokens, llm_completion_tokens,
				llm_cost_usd, llm_latency_ms, llm_persona_id,
				details
			) VALUES (
				@id, @occurred_at, @actor_user_id, @actor_role, @originated_by, @department_id,
				@event_type, @event_subtype, @target_kind, @target_id, @correlation_id,
				@outcome, @error_class,
				@llm_provider, @llm_model, @llm_prompt_tokens, @llm_completion_tokens,
				@llm_cost_usd, @llm_latency_ms, @llm_persona_id,
				@details::jsonb
			)
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("id", evt.Id);
		cmd.Parameters.AddWithValue("occurred_at", evt.OccurredAt);
		cmd.Parameters.AddWithValue("actor_user_id", evt.ActorUserId);
		AddNullable(cmd, "actor_role", evt.ActorRole, NpgsqlDbType.Text);
		AddNullable(cmd, "originated_by", evt.OriginatedBy, NpgsqlDbType.Uuid);
		AddNullable(cmd, "department_id", evt.DepartmentId, NpgsqlDbType.Uuid);
		cmd.Parameters.AddWithValue("event_type", evt.EventType);
		AddNullable(cmd, "event_subtype", evt.EventSubtype, NpgsqlDbType.Text);
		AddNullable(cmd, "target_kind", evt.TargetKind, NpgsqlDbType.Text);
		AddNullable(cmd, "target_id", evt.TargetId, NpgsqlDbType.Uuid);
		cmd.Parameters.AddWithValue("correlation_id", evt.CorrelationId);
		cmd.Parameters.AddWithValue("outcome", evt.Outcome.ToString());
		AddNullable(cmd, "error_class", evt.ErrorClass, NpgsqlDbType.Text);
		AddNullable(cmd, "llm_provider", evt.Llm?.Provider, NpgsqlDbType.Text);
		AddNullable(cmd, "llm_model", evt.Llm?.Model, NpgsqlDbType.Text);
		AddNullable(cmd, "llm_prompt_tokens", evt.Llm?.PromptTokens, NpgsqlDbType.Integer);
		AddNullable(cmd, "llm_completion_tokens", evt.Llm?.CompletionTokens, NpgsqlDbType.Integer);
		AddNullable(cmd, "llm_cost_usd", evt.Llm?.CostEstimateUsd, NpgsqlDbType.Numeric);
		AddNullable(cmd, "llm_latency_ms", evt.Llm?.LatencyMs, NpgsqlDbType.Integer);
		AddNullable(cmd, "llm_persona_id", evt.Llm?.PersonaId, NpgsqlDbType.Uuid);
		cmd.Parameters.AddWithValue("details", JsonSerializer.Serialize(evt.Details));

		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		_logger.LogDebug(
			"audit row written id={Id} type={EventType}/{EventSubtype} actor={ActorUserId}",
			evt.Id, evt.EventType, evt.EventSubtype, evt.ActorUserId);
	}

	/// <inheritdoc />
	public async Task<bool> IsLedgerReachableAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
			await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
			await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

			await using var cmd = new NpgsqlCommand("SELECT 1 FROM audit_events LIMIT 1", conn, tx);
			_ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (NpgsqlException ex)
		{
			_logger.LogWarning(ex, "Audit ledger probe failed: {Message}", ex.Message);
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<AuditEvent>> RecentAsync(
		int take,
		CancellationToken cancellationToken = default)
	{
		if (take <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(take), take, "take must be positive.");
		}

		await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await RlsSessionPusher.PushAsync(conn, RlsSessionContext.System(), cancellationToken).ConfigureAwait(false);

		const string sql = """
			SELECT id, occurred_at, actor_user_id, actor_role, originated_by, department_id,
				event_type, event_subtype, target_kind, target_id, correlation_id,
				outcome, error_class,
				llm_provider, llm_model, llm_prompt_tokens, llm_completion_tokens,
				llm_cost_usd, llm_latency_ms, llm_persona_id,
				details
			FROM audit_events
			ORDER BY occurred_at DESC
			LIMIT @take
			""";

		await using var cmd = new NpgsqlCommand(sql, conn, tx);
		cmd.Parameters.AddWithValue("take", take);

		// Scoped reader: Npgsql forbids running another command on the same
		// connection while a data reader is open; tx.CommitAsync counts as
		// a command and throws NpgsqlOperationInProgressException if the
		// reader is still alive at commit time. Dispose explicitly before
		// committing.
		var results = new List<AuditEvent>(capacity: Math.Min(take, 64));
		await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				results.Add(MapEvent(reader));
			}
		}

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
		return results;
	}

	private static AuditEvent MapEvent(NpgsqlDataReader r)
	{
		LlmTelemetry? llm = null;
		if (!r.IsDBNull(13))
		{
			llm = new LlmTelemetry(
				Provider: r.GetString(13),
				Model: r.IsDBNull(14) ? string.Empty : r.GetString(14),
				PromptTokens: r.IsDBNull(15) ? 0 : r.GetInt32(15),
				CompletionTokens: r.IsDBNull(16) ? 0 : r.GetInt32(16),
				CostEstimateUsd: r.IsDBNull(17) ? null : r.GetDecimal(17),
				LatencyMs: r.IsDBNull(18) ? 0 : r.GetInt32(18),
				PersonaId: r.IsDBNull(19) ? null : r.GetGuid(19));
		}

		var detailsJson = r.IsDBNull(20) ? "{}" : r.GetString(20);
		var details = JsonSerializer.Deserialize<Dictionary<string, object?>>(detailsJson)
			?? new Dictionary<string, object?>();

		return new AuditEvent(
			Id: r.GetGuid(0),
			OccurredAt: r.GetFieldValue<DateTimeOffset>(1),
			ActorUserId: r.GetGuid(2),
			ActorRole: r.IsDBNull(3) ? null : r.GetString(3),
			OriginatedBy: r.IsDBNull(4) ? null : r.GetGuid(4),
			DepartmentId: r.IsDBNull(5) ? null : r.GetGuid(5),
			EventType: r.GetString(6),
			EventSubtype: r.IsDBNull(7) ? null : r.GetString(7),
			TargetKind: r.IsDBNull(8) ? null : r.GetString(8),
			TargetId: r.IsDBNull(9) ? null : r.GetGuid(9),
			CorrelationId: r.GetGuid(10),
			Outcome: Enum.Parse<EventOutcome>(r.GetString(11)),
			ErrorClass: r.IsDBNull(12) ? null : r.GetString(12),
			Llm: llm,
			Details: details);
	}

	private static void AddNullable<T>(NpgsqlCommand cmd, string name, T? value, NpgsqlDbType type)
		where T : struct
	{
		var p = cmd.Parameters.Add(name, type);
		p.Value = value.HasValue ? value.Value : DBNull.Value;
	}

	private static void AddNullable(NpgsqlCommand cmd, string name, string? value, NpgsqlDbType type)
	{
		var p = cmd.Parameters.Add(name, type);
		p.Value = value is null ? DBNull.Value : value;
	}
}
