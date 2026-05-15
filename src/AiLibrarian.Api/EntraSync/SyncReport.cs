namespace AiLibrarian.Api.EntraSync;

/// <summary>
/// Outcome of one <see cref="EntraGroupSyncService.RunAsync"/> pass.
/// Returned to the admin endpoint and surfaced in the audit ledger.
/// </summary>
/// <param name="StartedAt">When the run started.</param>
/// <param name="DurationMs">How long the run took, in milliseconds.</param>
/// <param name="GroupsProcessed">Mappings the run attempted.</param>
/// <param name="GroupsSucceeded">Mappings that reconciled without error.</param>
/// <param name="GroupsFailed">Mappings whose Graph call or DB write failed.</param>
/// <param name="GrantsAdded">Newly-inserted authorization rows across all groups.</param>
/// <param name="GrantsRevoked">Authorization rows deleted because their owner left the group.</param>
/// <param name="Mappings">Per-mapping detail for the audit log.</param>
public sealed record SyncReport(
	DateTimeOffset StartedAt,
	long DurationMs,
	int GroupsProcessed,
	int GroupsSucceeded,
	int GroupsFailed,
	int GrantsAdded,
	int GrantsRevoked,
	IReadOnlyList<SyncMappingResult> Mappings);

/// <summary>One mapping's result inside a <see cref="SyncReport"/>.</summary>
/// <param name="GroupObjectId">The Entra group object id.</param>
/// <param name="DisplayLabel">Operator-supplied label (audit anchor).</param>
/// <param name="SourceGroupId">The value written into <c>user_authorizations.source_group_id</c>.</param>
/// <param name="Members">Entra members observed at sync time.</param>
/// <param name="GrantsAdded">Newly-inserted authorization rows.</param>
/// <param name="GrantsRevoked">Rows deleted by the reconciliation pass.</param>
/// <param name="Error">Null on success; populated on failure.</param>
public sealed record SyncMappingResult(
	string GroupObjectId,
	string? DisplayLabel,
	string SourceGroupId,
	int Members,
	int GrantsAdded,
	int GrantsRevoked,
	string? Error);
