namespace AiLibrarian.Domain.Sources;

/// <summary>
/// Lifecycle status of a <see cref="Source"/>, derived from the
/// <c>approved_at</c> and <c>soft_deleted_at</c> columns on
/// <c>sources</c>. The architecture documents a richer enumeration
/// (<c>rejected</c>, <c>quarantined</c>) that is not yet schema-backed;
/// callers wanting those distinctions should add explicit columns
/// before relying on them.
/// </summary>
public enum SourceStatus
{
	/// <summary>Submitted but not yet approved by a Reviewer/Librarian.</summary>
	Pending = 0,

	/// <summary>Approved and indexable. Visible to retrieval per RLS.</summary>
	Approved = 1,

	/// <summary>Soft-deleted (ADR 0008 tier 1) — hidden from RLS reads but retained for audit.</summary>
	Deleted = 2,
}
