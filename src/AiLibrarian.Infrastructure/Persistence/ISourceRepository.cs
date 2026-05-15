using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Read-side repository for <see cref="Source"/>. Phase 1 ships the
/// read surface only; the ingest pipeline (Phase 1+) will add a
/// separate write-side once the canonicalize/chunk/embed stages are
/// real. Every call pushes the supplied <see cref="RlsSessionContext"/>
/// through <c>RlsSessionPusher</c> so the same caller-identity gate
/// that applies to direct SQL applies here.
/// </summary>
public interface ISourceRepository
{
	/// <summary>
	/// Fetch a single source by id, RLS-filtered to the caller's
	/// authorized set. Returns <see langword="null"/> when the source
	/// does not exist <b>or</b> when RLS hides it from the caller — by
	/// design, the two cases are indistinguishable to the API layer
	/// (no probing for hidden sources).
	/// </summary>
	Task<Source?> GetByIdAsync(
		RlsSessionContext context,
		Guid sourceId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// List sources visible to the caller, ordered by most-recently-
	/// created first. Soft-deleted rows are excluded.
	/// <paramref name="departmentId"/> is an optional narrowing filter;
	/// when null, every source the RLS read predicate authorizes is
	/// included (typically: the caller's home departments + any
	/// <c>Internal</c> sources from other departments + active
	/// <c>source_shares</c>).
	/// </summary>
	Task<IReadOnlyList<Source>> ListAsync(
		RlsSessionContext context,
		Guid? departmentId,
		int limit,
		int offset,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Look up an existing active source in <paramref name="departmentId"/>
	/// with the given canonical-content checksum. Used by the portal
	/// upload route to dedupe re-uploads of identical content; returns
	/// the existing row instead of creating a duplicate. Honors RLS —
	/// callers see only checksums on sources they're authorized to
	/// read, which by design hides matches in departments the caller
	/// can't see (avoids leaking the existence of hidden sources).
	/// </summary>
	Task<Source?> FindByChecksumAsync(
		RlsSessionContext context,
		Guid departmentId,
		string checksumSha256,
		CancellationToken cancellationToken = default);
}
