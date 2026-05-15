using System.Collections.Concurrent;

using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Quality;

/// <summary>
/// In-memory <see cref="IClaimGradeSink"/> for the eval harness and
/// for dev-without-Postgres flows. Process-local; resets on restart.
/// Phase 2 ships <c>PostgresClaimGradeSink</c> in
/// <c>AiLibrarian.Infrastructure</c> as the durable counterpart;
/// callers should depend on <see cref="IClaimGradeSink"/> and let DI
/// swap which one runs.
/// </summary>
public sealed class InMemoryClaimGradeSink : IClaimGradeSink
{
	private readonly ConcurrentDictionary<Guid, ClaimGrade> _grades = new();

	/// <summary>Adds or replaces a grade. Sync shape retained for the pre-Phase-2 callers.</summary>
	public void Record(ClaimGrade grade)
	{
		ArgumentNullException.ThrowIfNull(grade);
		_grades[grade.ClaimId] = grade;
	}

	/// <summary>True when a grade has been recorded for this claim.</summary>
	public bool TryGet(Guid claimId, out ClaimGrade grade) => _grades.TryGetValue(claimId, out grade!);

	/// <summary>Snapshot of every grade currently recorded.</summary>
	public IReadOnlyCollection<ClaimGrade> Snapshot() => _grades.Values.ToList();

	/// <summary>Clears every grade. Tests only.</summary>
	public void Reset() => _grades.Clear();

	/// <inheritdoc />
	public Task RecordAsync(ClaimGrade grade, string graderVersion, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(grade);
		// graderVersion is ignored in-memory -- the in-memory sink keeps only the latest verdict per claim.
		Record(grade);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task<ClaimGrade?> GetLatestAsync(Guid claimId, CancellationToken cancellationToken = default)
		=> Task.FromResult(_grades.TryGetValue(claimId, out var grade) ? grade : null);

	/// <inheritdoc />
	public Task<IReadOnlyCollection<ClaimGrade>> SnapshotAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyCollection<ClaimGrade>>(_grades.Values.ToList());
}
