using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Sources;
using AiLibrarian.Domain.Users;
using AiLibrarian.Infrastructure.Rls;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Dev-without-Postgres fallback for <see cref="ISourceRepository"/>.
/// Always returns <see langword="null"/>; the API layer translates that
/// to a 503 with a "Postgres not configured" hint, mirroring the
/// behavior of <c>NullHybridChunkSearch</c>.
/// </summary>
internal sealed class NullSourceRepository : ISourceRepository
{
	public Task<Source?> GetByIdAsync(
		RlsSessionContext context,
		Guid sourceId,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<Source?>(null);

	public Task<IReadOnlyList<Source>> ListAsync(
		RlsSessionContext context,
		Guid? departmentId,
		int limit,
		int offset,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<Source>>(Array.Empty<Source>());

	public Task<Source?> FindByChecksumAsync(
		RlsSessionContext context,
		Guid departmentId,
		string checksumSha256,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<Source?>(null);
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="IDepartmentRepository"/>.
/// Returns an empty list so callers don't see a 503 just to enumerate
/// departments — the API layer separately reports configuration state
/// via <c>/health</c>.
/// </summary>
internal sealed class NullDepartmentRepository : IDepartmentRepository
{
	public Task<IReadOnlyList<Department>> ListActiveAsync(
		RlsSessionContext context,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<Department>>(Array.Empty<Department>());
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="ISourceWriter"/>.
/// Throws on every call — write-path callers must check Postgres
/// configuration before invoking, and the API surface translates this
/// into the "Sources not configured" 503 error rather than a stack
/// trace.
/// </summary>
internal sealed class NullSourceWriter : ISourceWriter
{
	public Task<Guid> CreateAsync(
		RlsSessionContext context,
		SourceSubmission submission,
		CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Source writes require ConnectionStrings:Postgres to be configured.");

	public Task<bool> UpdateChecksumAndSizeAsync(
		RlsSessionContext context,
		Guid sourceId,
		string checksumSha256,
		long sizeBytes,
		CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Source writes require ConnectionStrings:Postgres to be configured.");
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="IUserDirectory"/>.
/// EnsureUserAsync returns a synthetic in-memory row so dev-mode flows
/// don't 500 when the caller doesn't have Postgres wired; the API still
/// reports degraded mode via <c>/health</c>.
/// </summary>
internal sealed class NullUserDirectory : IUserDirectory
{
	public Task<UserRow> EnsureUserAsync(
		Guid oid,
		string? email,
		string? displayName,
		bool isEmployee,
		CancellationToken cancellationToken = default)
		=> Task.FromResult(new UserRow(
			Id: oid,
			Email: email,
			DisplayName: displayName,
			IsEmployee: isEmployee,
			DeactivatedAt: null,
			CreatedAt: DateTimeOffset.UtcNow));

	public Task<UserDirectoryProjection?> GetProjectionAsync(
		Guid oid,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<UserDirectoryProjection?>(null);
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="IUserAuthorizationWriter"/>.
/// Every write throws -- the group-sync job has no useful work without a
/// real database, and silent no-ops would hide misconfigurations.
/// </summary>
internal sealed class NullUserAuthorizationWriter : IUserAuthorizationWriter
{
	public Task<bool> GrantAsync(Guid userId, Guid? departmentId, Role role, string sourceGroupId, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"User-authorization writes require ConnectionStrings:Postgres to be configured.");

	public Task<int> ReconcileAsync(string sourceGroupId, IReadOnlyCollection<Guid> keepUserIds, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"User-authorization writes require ConnectionStrings:Postgres to be configured.");

	public Task<IReadOnlyList<UserAuthorization>> ListBySourceGroupAsync(string sourceGroupId, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<UserAuthorization>>(Array.Empty<UserAuthorization>());
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="IClaimGradeSink"/>.
/// Process-local dictionary; persists nothing across restarts. Sibling
/// to <c>AiLibrarian.Quality.InMemoryClaimGradeSink</c> -- kept here
/// so callers that already depend on <c>AiLibrarian.Infrastructure</c>
/// don't need to also pull in the Quality project just for the dev sink.
/// </summary>
internal sealed class NullClaimGradeSink : IClaimGradeSink
{
	private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ClaimGrade> _grades = new();

	public Task RecordAsync(ClaimGrade grade, string graderVersion, CancellationToken cancellationToken = default)
	{
		_grades[grade.ClaimId] = grade;
		return Task.CompletedTask;
	}

	public Task<ClaimGrade?> GetLatestAsync(Guid claimId, CancellationToken cancellationToken = default)
		=> Task.FromResult(_grades.TryGetValue(claimId, out var g) ? g : null);

	public Task<IReadOnlyCollection<ClaimGrade>> SnapshotAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyCollection<ClaimGrade>>(_grades.Values.ToList());
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="IChunkLookup"/>. Returns
/// an empty dictionary -- the citation validator's rule-2 catches the
/// "chunk_missing" case correctly and rules 3-5 don't fire when no
/// chunks resolve. The API surfaces degraded mode via <c>/health</c>.
/// </summary>
internal sealed class NullChunkLookup : IChunkLookup
{
	public Task<IReadOnlyDictionary<Guid, ChunkRef>> ResolveAsync(
		IReadOnlyCollection<Guid> chunkIds,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyDictionary<Guid, ChunkRef>>(new Dictionary<Guid, ChunkRef>());
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="IChunkSampler"/>.
/// Returns an empty sample; the candidate-discovery endpoint surfaces
/// this as "no candidates" rather than a 503 -- the dev experience
/// is "no data" not "broken".
/// </summary>
internal sealed class NullChunkSampler : IChunkSampler
{
	public Task<IReadOnlyList<SampledChunk>> SampleAsync(
		Guid departmentId,
		int limit,
		int maxCharsPerChunk = 4096,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<SampledChunk>>(Array.Empty<SampledChunk>());
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="IChunkContentReader"/>.
/// Returns an empty dictionary; the WikiSourcePoolBuilder falls back
/// to the hit's <c>Excerpt</c> in that case so dev-mode flows still
/// work, just with the truncated content.
/// </summary>
internal sealed class NullChunkContentReader : IChunkContentReader
{
	public Task<IReadOnlyDictionary<Guid, string>> ReadContentAsync(
		IReadOnlyCollection<Guid> chunkIds,
		int maxCharsPerChunk = 4096,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="AiLibrarian.Domain.Wiki.IWikiRevisionWriter"/>.
/// Throws on every write -- there's no useful in-memory persistence for
/// wiki revisions (the chain of foreign keys would need an in-memory
/// shadow of the entire wiki schema). Operators get a clear "configure
/// Postgres" error rather than a silent no-op.
/// </summary>
internal sealed class NullWikiRevisionWriter : AiLibrarian.Domain.Wiki.IWikiRevisionWriter
{
	public Task<Guid> CommitAsync(AiLibrarian.Domain.Wiki.WikiRevisionDraft draft, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki revision writes require ConnectionStrings:Postgres to be configured.");
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="AiLibrarian.Domain.Wiki.IWikiProposalWriter"/>.
/// Same "configure Postgres" throw -- proposal lifecycle is database-bound
/// and silent no-ops would hide misconfigurations.
/// </summary>
internal sealed class NullWikiProposalWriter : AiLibrarian.Domain.Wiki.IWikiProposalWriter
{
	public Task<Guid> CreateAsync(AiLibrarian.Domain.Wiki.WikiProposedRevision proposal, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki proposal writes require ConnectionStrings:Postgres to be configured.");

	public Task<Guid?> DecideAsync(Guid proposalId, AiLibrarian.Domain.Wiki.ProposalState decision, Guid decidedBy, string? reason, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki proposal writes require ConnectionStrings:Postgres to be configured.");

	public Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult(0);

	public Task<AiLibrarian.Domain.Wiki.BulkRejectOutcome> BulkRejectAsync(
		IReadOnlyCollection<Guid> proposalIds,
		Guid decidedBy,
		string reason,
		CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki proposal writes require ConnectionStrings:Postgres to be configured.");
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="AiLibrarian.Domain.Wiki.IWikiProposalReader"/>.
/// Returns null / empty so the admin endpoints respond cleanly with
/// "no proposals" rather than 500 in dev mode.
/// </summary>
internal sealed class NullWikiProposalReader : AiLibrarian.Domain.Wiki.IWikiProposalReader
{
	public Task<AiLibrarian.Domain.Wiki.WikiProposedRevision?> GetAsync(Guid proposalId, CancellationToken cancellationToken = default)
		=> Task.FromResult<AiLibrarian.Domain.Wiki.WikiProposedRevision?>(null);

	public Task<IReadOnlyList<AiLibrarian.Domain.Wiki.WikiProposedRevision>> ListAsync(AiLibrarian.Domain.Wiki.ProposalState? state, int limit, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<AiLibrarian.Domain.Wiki.WikiProposedRevision>>(Array.Empty<AiLibrarian.Domain.Wiki.WikiProposedRevision>());

	public Task<IReadOnlyList<AiLibrarian.Domain.Wiki.WikiProposedRevision>> ListDecidedAsync(
		Guid? decidedBy,
		DateTimeOffset? since,
		int limit,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<AiLibrarian.Domain.Wiki.WikiProposedRevision>>(Array.Empty<AiLibrarian.Domain.Wiki.WikiProposedRevision>());
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="AiLibrarian.Domain.Wiki.IWikiPageReader"/>.
/// Reports every page as unlocked so the maintainer commits directly
/// in dev mode (no proposal queue to land in).
/// </summary>
internal sealed class NullWikiPageReader : AiLibrarian.Domain.Wiki.IWikiPageReader
{
	public Task<bool> IsLockedAsync(Guid pageId, CancellationToken cancellationToken = default)
		=> Task.FromResult(false);

	public Task<IReadOnlySet<string>> ListSlugsAsync(Guid departmentId, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Dev-without-Postgres fallback for <see cref="AiLibrarian.Domain.Wiki.IWikiPageWriter"/>.
/// Throws on every call — auto-page-discovery only makes sense against
/// a real schema. The API surface translates this into a 503 "Wiki not
/// configured" response rather than a stack trace.
/// </summary>
internal sealed class NullWikiPageWriter : AiLibrarian.Domain.Wiki.IWikiPageWriter
{
	public Task<AiLibrarian.Domain.Wiki.EnsurePageResult> EnsurePageAsync(
		AiLibrarian.Domain.Wiki.EnsurePageRequest request,
		CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki page writes require ConnectionStrings:Postgres to be configured.");

	public Task<bool> RenameAsync(Guid pageId, string newTitle, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki page writes require ConnectionStrings:Postgres to be configured.");

	public Task<bool> SetLockedAsync(Guid pageId, bool locked, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki page writes require ConnectionStrings:Postgres to be configured.");

	public Task<bool> SoftDeleteAsync(Guid pageId, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki page writes require ConnectionStrings:Postgres to be configured.");

	public Task<AiLibrarian.Domain.Wiki.RestorePageResult> RestoreAsync(Guid pageId, CancellationToken cancellationToken = default)
		=> throw new InvalidOperationException(
			"Wiki page writes require ConnectionStrings:Postgres to be configured.");
}
