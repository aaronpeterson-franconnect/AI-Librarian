namespace AiLibrarian.Domain.Sources;

/// <summary>
/// A submitted source artifact. Maps to the <c>sources</c> table from
/// changeset <c>0004-sources-1</c> and is governed by RLS policies in
/// <c>0099-rls-policies.sql</c> (classification-driven reads,
/// department + role writes).
/// </summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="DepartmentId">Owning department (corpus boundary, ADR 0005).</param>
/// <param name="Classification">Default access boundary, ADR 0011.</param>
/// <param name="Title">Human-readable title supplied at submit time.</param>
/// <param name="Uri">Optional canonical URI; null when the source is blob-backed.</param>
/// <param name="ContentType">MIME type, e.g. <c>application/pdf</c>.</param>
/// <param name="ChecksumSha256">Content fingerprint, hex-encoded; null until ingest computes it.</param>
/// <param name="SizeBytes">Byte size of the canonicalized payload; null when unknown.</param>
/// <param name="ContributedBy">User who submitted the source.</param>
/// <param name="ApprovedBy">Reviewer/Librarian who approved it; null until approval.</param>
/// <param name="ApprovedAt">When approval landed; null until approval.</param>
/// <param name="SoftDeletedAt">Tier-1 deletion timestamp per ADR 0008; null when active.</param>
/// <param name="CreatedAt">Row creation timestamp.</param>
/// <param name="UpdatedAt">Last update timestamp (mutating attribute changes only — citations, chunks, and the like live in their own tables).</param>
public sealed record Source(
	Guid Id,
	Guid DepartmentId,
	Classification Classification,
	string Title,
	string? Uri,
	string ContentType,
	string? ChecksumSha256,
	long? SizeBytes,
	Guid ContributedBy,
	Guid? ApprovedBy,
	DateTimeOffset? ApprovedAt,
	DateTimeOffset? SoftDeletedAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt)
{
	/// <summary>
	/// Derived lifecycle status. The <c>sources</c> schema does not
	/// carry an explicit <c>status</c> column today (per
	/// <c>0004-sources-1</c>); the architecture's full enumeration
	/// (<c>pending</c>, <c>approved</c>, <c>rejected</c>,
	/// <c>quarantined</c>, <c>deleted</c>) is reduced to three states
	/// derivable from <see cref="ApprovedAt"/> and
	/// <see cref="SoftDeletedAt"/>. Future migrations may add explicit
	/// columns for the rejected/quarantined states.
	/// </summary>
	public SourceStatus Status => SoftDeletedAt.HasValue
		? SourceStatus.Deleted
		: ApprovedAt.HasValue
			? SourceStatus.Approved
			: SourceStatus.Pending;
}
