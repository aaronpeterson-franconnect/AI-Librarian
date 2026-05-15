using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Write-side contract for the <c>sources</c> table. Decoupled from
/// <see cref="ISourceRepository"/> so the API surface can be stricter
/// about who can mutate the corpus (Phase 1: portal upload + MCP
/// submit) while readers stay broadly available.
///
/// <para>
/// Mutation semantics in Phase 1:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="CreateAsync"/> inserts a new row with
///   <c>checksum_sha256</c> + <c>size_bytes</c> NULL — the worker fills
///   them later via <see cref="UpdateChecksumAndSizeAsync"/>. Phase 1
///   auto-approves contributor uploads (<c>approved_at = now()</c>);
///   the formal approval queue lands in Phase 2 with the wiki layer.
///   </description></item>
///   <item><description><see cref="UpdateChecksumAndSizeAsync"/> is the
///   ingest worker's commit point: a successful canonicalize updates
///   the source with its content fingerprint and byte count.
///   </description></item>
/// </list>
/// </summary>
public interface ISourceWriter
{
	/// <summary>
	/// Insert a new <c>sources</c> row from <paramref name="submission"/>
	/// and return its identifier. The RLS write predicate gates the
	/// caller to <c>app_contributor_depts</c> on the target department
	/// (or admin); the writer translates a policy violation into an
	/// <see cref="UnauthorizedSourceWriteException"/>.
	/// </summary>
	Task<Guid> CreateAsync(
		RlsSessionContext context,
		SourceSubmission submission,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Update the post-canonicalize fields. Idempotent: re-running with
	/// the same checksum is a no-op. Returns <see langword="true"/> when
	/// the row matched and was updated, <see langword="false"/> when the
	/// id was missing or RLS hid it.
	/// </summary>
	Task<bool> UpdateChecksumAndSizeAsync(
		RlsSessionContext context,
		Guid sourceId,
		string checksumSha256,
		long sizeBytes,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when a source-write call is blocked by the RLS policy —
/// i.e. the caller is not a contributor on the target department.
/// </summary>
public sealed class UnauthorizedSourceWriteException : Exception
{
	/// <summary>Creates the exception.</summary>
	public UnauthorizedSourceWriteException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with an inner cause.</summary>
	public UnauthorizedSourceWriteException(string message, Exception inner)
		: base(message, inner)
	{
	}
}
