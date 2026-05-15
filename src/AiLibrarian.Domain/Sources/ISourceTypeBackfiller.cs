namespace AiLibrarian.Domain.Sources;

/// <summary>
/// One-shot maintenance surface for retroactively classifying source
/// rows whose <c>source_type</c> is NULL (i.e. created before
/// migration 0028 + the classifier wiring). Designed to be called
/// from an admin endpoint in bounded batches so the operator can run
/// it incrementally and observe progress; re-running is safe because
/// the writer only touches rows that are still NULL.
///
/// <para><b>Why an interface rather than a SQL one-liner.</b> The
/// classifier is the same code path as the live INSERT classifier
/// (<see cref="SourceTypeClassifier"/>), so any future change to the
/// rule cascade automatically applies to backfill too. A
/// <c>UPDATE ... SET source_type = ...</c> SQL script would drift.</para>
///
/// <para>Runs in system admin RLS context: the maintenance worker
/// must see soft-deleted rows too (they still benefit from
/// classification for audit), and dept-scoped reads would miss
/// cross-department rows.</para>
/// </summary>
public interface ISourceTypeBackfiller
{
	/// <summary>
	/// Classify up to <paramref name="batchSize"/> unclassified
	/// (<c>source_type IS NULL</c>) source rows and return a snapshot
	/// of the work done. Operators call this repeatedly until
	/// <see cref="SourceTypeBackfillOutcome.RemainingUnclassified"/>
	/// hits zero.
	/// </summary>
	/// <param name="batchSize">Max rows to classify in this call. Implementations clamp to a sane upper bound.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<SourceTypeBackfillOutcome> BackfillBatchAsync(
		int batchSize,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Snapshot of one backfill batch.
/// </summary>
/// <param name="ClassifiedThisCall">How many rows this call transitioned from NULL to a classified value.</param>
/// <param name="RemainingUnclassified">How many rows still have <c>source_type IS NULL</c> after this call. Operators stop calling when this reaches zero.</param>
/// <param name="ClassificationCounts">Per-<see cref="SourceType"/> count of how many rows were classified into each bucket this call. Diagnostic; surfaces in the admin response so operators can spot a runaway "everything is document" cascade.</param>
public sealed record SourceTypeBackfillOutcome(
	int ClassifiedThisCall,
	long RemainingUnclassified,
	IReadOnlyDictionary<string, int> ClassificationCounts);
