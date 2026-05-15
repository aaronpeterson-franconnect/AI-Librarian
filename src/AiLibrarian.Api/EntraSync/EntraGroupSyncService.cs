using System.Diagnostics;

using AiLibrarian.Auditing;
using AiLibrarian.Domain;
using AiLibrarian.Domain.Users;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.EntraSync;

/// <summary>
/// Reconciles Entra group membership into <c>user_authorizations</c>.
/// Each configured <see cref="EntraGroupMapping"/> drives one round:
/// <list type="number">
///   <item>Pull the group's current members from Graph.</item>
///   <item>Upsert a grant row per member (idempotent via the unique
///         indices on <c>user_authorizations</c>).</item>
///   <item>Revoke any row tagged with this mapping's
///         <c>source_group_id</c> whose user is no longer in the
///         group.</item>
///   <item>Audit the count of additions / revocations.</item>
/// </list>
///
/// <para>Grants are tagged with a stable <c>source_group_id</c> built
/// from the Graph object id, so manual <c>bootstrap-*</c> grants from
/// <c>Bootstrap-UserAuthorizations.ps1</c> are not touched by the
/// reconciliation pass — only rows the sync job itself wrote.</para>
///
/// <para>Per-group failures are isolated: a Graph 404 or DB error on
/// one mapping doesn't halt the others. The <see cref="SyncReport"/>
/// names which succeeded and which failed.</para>
/// </summary>
public sealed class EntraGroupSyncService
{
	private const string SourceGroupPrefix = "entra-sync:";

	private readonly IGraphMembershipClient _graph;
	private readonly IUserAuthorizationWriter _writer;
	private readonly IAuditWriter _auditWriter;
	private readonly IOptions<EntraGroupSyncOptions> _options;
	private readonly ILogger<EntraGroupSyncService> _logger;
	private readonly TimeProvider _clock;

	/// <summary>Creates the service.</summary>
	public EntraGroupSyncService(
		IGraphMembershipClient graph,
		IUserAuthorizationWriter writer,
		IAuditWriter auditWriter,
		IOptions<EntraGroupSyncOptions> options,
		ILogger<EntraGroupSyncService> logger,
		TimeProvider? clock = null)
	{
		_graph = graph;
		_writer = writer;
		_auditWriter = auditWriter;
		_options = options;
		_logger = logger;
		_clock = clock ?? TimeProvider.System;
	}

	/// <summary>Run one sync pass. Returns a report; never throws.</summary>
	public async Task<SyncReport> RunAsync(CancellationToken cancellationToken = default)
	{
		var startedAt = _clock.GetUtcNow();
		var sw = Stopwatch.StartNew();

		var opts = _options.Value;
		if (!opts.Enabled || opts.GroupMappings.Count == 0)
		{
			_logger.LogInformation("EntraGroupSync: disabled or no mappings; nothing to do.");
			return new SyncReport(
				StartedAt: startedAt,
				DurationMs: 0,
				GroupsProcessed: 0,
				GroupsSucceeded: 0,
				GroupsFailed: 0,
				GrantsAdded: 0,
				GrantsRevoked: 0,
				Mappings: Array.Empty<SyncMappingResult>());
		}

		var mappings = new List<SyncMappingResult>(opts.GroupMappings.Count);
		var totalAdded = 0;
		var totalRevoked = 0;
		var succeeded = 0;
		var failed = 0;

		foreach (var mapping in opts.GroupMappings)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (!TryValidate(mapping, out var validatedDeptId, out var validationError))
			{
				_logger.LogWarning(
					"EntraGroupSync: skipping invalid mapping group={Group} -- {Error}",
					mapping.GroupObjectId,
					validationError);
				mappings.Add(new SyncMappingResult(
					GroupObjectId: mapping.GroupObjectId,
					DisplayLabel: mapping.DisplayLabel,
					SourceGroupId: BuildSourceGroupId(mapping),
					Members: 0,
					GrantsAdded: 0,
					GrantsRevoked: 0,
					Error: validationError));
				failed++;
				continue;
			}

			var sourceGroupId = BuildSourceGroupId(mapping);
			try
			{
				var members = await _graph.ListGroupMemberOidsAsync(mapping.GroupObjectId, cancellationToken).ConfigureAwait(false);

				var inserted = 0;
				foreach (var memberOid in members)
				{
					var wasInsert = await _writer.GrantAsync(
						memberOid,
						validatedDeptId,
						mapping.Role,
						sourceGroupId,
						cancellationToken).ConfigureAwait(false);
					if (wasInsert)
					{
						inserted++;
					}
				}

				var revoked = await _writer.ReconcileAsync(sourceGroupId, members, cancellationToken).ConfigureAwait(false);

				totalAdded += inserted;
				totalRevoked += revoked;
				succeeded++;
				mappings.Add(new SyncMappingResult(
					GroupObjectId: mapping.GroupObjectId,
					DisplayLabel: mapping.DisplayLabel,
					SourceGroupId: sourceGroupId,
					Members: members.Count,
					GrantsAdded: inserted,
					GrantsRevoked: revoked,
					Error: null));

				if (inserted > 0 || revoked > 0)
				{
					await EmitGroupAuditAsync(mapping, sourceGroupId, members.Count, inserted, revoked, cancellationToken).ConfigureAwait(false);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogError(ex,
					"EntraGroupSync mapping failed group={Group} role={Role}",
					mapping.GroupObjectId,
					mapping.Role);
				failed++;
				mappings.Add(new SyncMappingResult(
					GroupObjectId: mapping.GroupObjectId,
					DisplayLabel: mapping.DisplayLabel,
					SourceGroupId: sourceGroupId,
					Members: 0,
					GrantsAdded: 0,
					GrantsRevoked: 0,
					Error: ex.Message));
			}
		}

		sw.Stop();

		var report = new SyncReport(
			StartedAt: startedAt,
			DurationMs: sw.ElapsedMilliseconds,
			GroupsProcessed: opts.GroupMappings.Count,
			GroupsSucceeded: succeeded,
			GroupsFailed: failed,
			GrantsAdded: totalAdded,
			GrantsRevoked: totalRevoked,
			Mappings: mappings);

		await EmitRunAuditAsync(report, cancellationToken).ConfigureAwait(false);
		return report;
	}

	private static bool TryValidate(EntraGroupMapping mapping, out Guid? departmentId, out string? error)
	{
		departmentId = null;

		if (string.IsNullOrWhiteSpace(mapping.GroupObjectId)
			|| !Guid.TryParse(mapping.GroupObjectId, out _))
		{
			error = "GroupObjectId must be a GUID.";
			return false;
		}

		if (mapping.Role == Role.Admin)
		{
			if (!string.IsNullOrWhiteSpace(mapping.DepartmentId))
			{
				error = "Admin role must be system-wide (DepartmentId must be empty).";
				return false;
			}

			error = null;
			return true;
		}

		if (string.IsNullOrWhiteSpace(mapping.DepartmentId)
			|| !Guid.TryParse(mapping.DepartmentId, out var parsedDept)
			|| parsedDept == Guid.Empty)
		{
			error = $"Role {mapping.Role} requires a non-empty DepartmentId GUID.";
			return false;
		}

		departmentId = parsedDept;
		error = null;
		return true;
	}

	private static string BuildSourceGroupId(EntraGroupMapping mapping)
	{
		// Tag every grant with the Entra group's object id so the
		// bootstrap-* grants from Bootstrap-UserAuthorizations.ps1 are
		// outside this prefix and therefore left alone by the reconcile
		// pass.
		return $"{SourceGroupPrefix}{mapping.GroupObjectId}";
	}

	private Task EmitGroupAuditAsync(
		EntraGroupMapping mapping,
		string sourceGroupId,
		int memberCount,
		int added,
		int revoked,
		CancellationToken cancellationToken)
	{
		var details = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["group_object_id"] = mapping.GroupObjectId,
			["display_label"] = mapping.DisplayLabel,
			["source_group_id"] = sourceGroupId,
			["role"] = mapping.Role.ToString(),
			["department_id"] = mapping.DepartmentId,
			["member_count"] = memberCount,
			["grants_added"] = added,
			["grants_revoked"] = revoked,
		};

		var evt = new AuditEvent(
			Id: Guid.NewGuid(),
			OccurredAt: _clock.GetUtcNow(),
			ActorUserId: AuditConstants.SystemUserId,
			ActorRole: "system",
			OriginatedBy: null,
			DepartmentId: null,
			EventType: "user_auth",
			EventSubtype: "sync.group",
			TargetKind: "user_authorizations",
			TargetId: null,
			CorrelationId: Guid.NewGuid(),
			Outcome: EventOutcome.Success,
			ErrorClass: null,
			Llm: null,
			Details: details);

		return _auditWriter.WriteAsync(evt, AuditCriticality.Critical, cancellationToken);
	}

	private Task EmitRunAuditAsync(SyncReport report, CancellationToken cancellationToken)
	{
		var details = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["duration_ms"] = report.DurationMs,
			["groups_processed"] = report.GroupsProcessed,
			["groups_succeeded"] = report.GroupsSucceeded,
			["groups_failed"] = report.GroupsFailed,
			["grants_added"] = report.GrantsAdded,
			["grants_revoked"] = report.GrantsRevoked,
		};

		var evt = new AuditEvent(
			Id: Guid.NewGuid(),
			OccurredAt: report.StartedAt,
			ActorUserId: AuditConstants.SystemUserId,
			ActorRole: "system",
			OriginatedBy: null,
			DepartmentId: null,
			EventType: "user_auth",
			EventSubtype: "sync.run",
			TargetKind: "user_authorizations",
			TargetId: null,
			CorrelationId: Guid.NewGuid(),
			Outcome: report.GroupsFailed == 0 ? EventOutcome.Success : EventOutcome.Partial,
			ErrorClass: report.GroupsFailed > 0 ? "GroupSyncPartialFailure" : null,
			Llm: null,
			Details: details);

		return _auditWriter.WriteAsync(evt, AuditCriticality.Critical, cancellationToken);
	}
}
