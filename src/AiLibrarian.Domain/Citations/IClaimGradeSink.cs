namespace AiLibrarian.Domain.Citations;

/// <summary>
/// Persistence sink for <see cref="ClaimGrade"/> records emitted by
/// <see cref="IClaimGrader"/>. Two implementations:
/// <list type="bullet">
///   <item><c>InMemoryClaimGradeSink</c> in <c>AiLibrarian.Quality</c>
///         — process-local, used by the eval harness when running
///         without a database.</item>
///   <item><c>PostgresClaimGradeSink</c> in
///         <c>AiLibrarian.Infrastructure</c> — writes to the
///         Phase 2 <c>wiki_claim_grades</c> table.</item>
/// </list>
///
/// <para>The interface stays read+write-by-claim so callers don't have
/// to know which implementation they're talking to. Cross-claim
/// aggregation (computing inter-rater agreement, sweeping by
/// grader version) lives on top of <see cref="SnapshotAsync"/>.</para>
/// </summary>
public interface IClaimGradeSink
{
	/// <summary>
	/// Upsert a grade for the given claim. Postgres-backed
	/// implementations key on (claim_id, grader_version) so multiple
	/// grader passes coexist; in-memory implementations overwrite by
	/// claim only.
	/// </summary>
	Task RecordAsync(
		ClaimGrade grade,
		string graderVersion,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Fetch the most recent grade for a claim, or null when none has
	/// been recorded yet.
	/// </summary>
	Task<ClaimGrade?> GetLatestAsync(
		Guid claimId,
		CancellationToken cancellationToken = default);

	/// <summary>Snapshot every recorded grade. Pagination is a v2 concern.</summary>
	Task<IReadOnlyCollection<ClaimGrade>> SnapshotAsync(
		CancellationToken cancellationToken = default);
}
