using System.Security.Claims;

using AiLibrarian.Api.Auth;
using AiLibrarian.Domain.Wiki;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Test factory for the wiki-maintenance admin endpoints. Replaces the
/// Postgres-backed proposal reader/writer with in-memory stubs and
/// injects a configurable <see cref="ISessionContextResolver"/> so
/// tests can declare the calling principal (Admin / Reviewer /
/// Librarian / Anonymous) without juggling Entra wiring.
///
/// <para>The Testing environment skips <c>RequireAuthorization</c>
/// (the API's <c>entraConfigured</c> is false), which lets us drive
/// handlers directly and assert on the body-level role gates. The
/// audit writer falls through to the LLM gateway's <c>NoOpAuditWriter</c>
/// — calls land silently; tests don't assert on them here.</para>
///
/// <para>The factory also installs stubs for the three interfaces
/// the maintain + discover endpoints depend on
/// (<c>IWikiSourcePoolBuilder</c>, <c>IWikiRevisionNumberer</c>,
/// <c>IDanglingFacetReader</c>) so handler-level tests for those
/// endpoints can run without Postgres. Tests that only exercise the
/// proposal endpoints don't touch the stubs.</para>
/// </summary>
public sealed class WikiMaintenanceWebApplicationFactory : WebApplicationFactory<Program>
{
	/// <summary>Per-test session resolver stub. Mutate before issuing a request to change the caller.</summary>
	public StubSessionContextResolver Sessions { get; } = new();

	/// <summary>Per-test proposal reader stub.</summary>
	public StubWikiProposalReader Proposals { get; } = new();

	/// <summary>Per-test proposal writer stub.</summary>
	public StubWikiProposalWriter ProposalWrites { get; } = new();

	/// <summary>Per-test wiki page writer stub (rename + lock + ensure-page).</summary>
	public StubWikiPageWriter PageWrites { get; } = new();

	/// <summary>Per-test source-pool builder stub.</summary>
	public StubWikiSourcePoolBuilder SourcePool { get; } = new();

	/// <summary>Per-test revision-numberer stub.</summary>
	public StubWikiRevisionNumberer Numberer { get; } = new();

	/// <summary>Per-test dangling-facet reader stub.</summary>
	public StubDanglingFacetReader Dangling { get; } = new();

	/// <summary>Per-test wiki maintainer stub.</summary>
	public StubWikiMaintainer Maintainer { get; } = new();

	/// <summary>Per-test candidate-discovery generator stub.</summary>
	public StubWikiPageCandidateGenerator Candidates { get; } = new();

	/// <inheritdoc />
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");

		builder.ConfigureServices(services =>
		{
			services.Replace(ServiceDescriptor.Scoped<ISessionContextResolver>(_ => Sessions));
			services.Replace(ServiceDescriptor.Singleton<IWikiProposalReader>(_ => Proposals));
			services.Replace(ServiceDescriptor.Singleton<IWikiProposalWriter>(_ => ProposalWrites));
			services.Replace(ServiceDescriptor.Singleton<IWikiPageWriter>(_ => PageWrites));

			// IWikiSourcePoolBuilder / IWikiRevisionNumberer /
			// IDanglingFacetReader are Singleton in production; replace
			// each interface registration with the test stub.
			services.Replace(ServiceDescriptor.Singleton<global::AiLibrarian.Api.WikiMaintenance.IWikiSourcePoolBuilder>(_ => SourcePool));
			services.Replace(ServiceDescriptor.Singleton<global::AiLibrarian.Infrastructure.Persistence.IWikiRevisionNumberer>(_ => Numberer));
			services.Replace(ServiceDescriptor.Singleton<global::AiLibrarian.Infrastructure.Persistence.IDanglingFacetReader>(_ => Dangling));

			// IWikiMaintainer is Scoped in production; replace with our stub
			// (still scoped so the lifetime contract matches).
			services.Replace(ServiceDescriptor.Scoped<global::AiLibrarian.WikiMaintainer.IWikiMaintainer>(_ => Maintainer));
			services.Replace(ServiceDescriptor.Singleton<global::AiLibrarian.Domain.Wiki.IWikiPageCandidateGenerator>(_ => Candidates));
		});
	}
}

/// <summary>
/// Settable <see cref="ISessionContextResolver"/> for tests. Returns
/// <see cref="Current"/> verbatim on every <c>ResolveAsync</c> call.
/// Defaults to an anonymous principal so endpoints that gate on role
/// return 401/403 unless the test opts in.
/// </summary>
public sealed class StubSessionContextResolver : ISessionContextResolver
{
	/// <summary>The DTO returned by the next ResolveAsync call. Mutate to change the calling principal.</summary>
	public SessionContextBuilder.SessionContextDto Current { get; set; } = Anonymous();

	/// <summary>Convenience: produce an anonymous DTO.</summary>
	public static SessionContextBuilder.SessionContextDto Anonymous() => new(
		UserId: Guid.Empty,
		IsAuthenticated: false,
		IsEmployee: false,
		HomeDepartmentIds: Array.Empty<Guid>(),
		ContributorDepartmentIds: Array.Empty<Guid>(),
		ReviewerDepartmentIds: Array.Empty<Guid>(),
		LibrarianDepartmentIds: Array.Empty<Guid>(),
		IsAdmin: false,
		PersonaId: null);

	/// <summary>Convenience: produce an Admin DTO.</summary>
	public static SessionContextBuilder.SessionContextDto Admin(Guid? userId = null) => new(
		UserId: userId ?? Guid.NewGuid(),
		IsAuthenticated: true,
		IsEmployee: true,
		HomeDepartmentIds: Array.Empty<Guid>(),
		ContributorDepartmentIds: Array.Empty<Guid>(),
		ReviewerDepartmentIds: Array.Empty<Guid>(),
		LibrarianDepartmentIds: Array.Empty<Guid>(),
		IsAdmin: true,
		PersonaId: null);

	/// <summary>Convenience: produce a Reviewer DTO on the given department.</summary>
	public static SessionContextBuilder.SessionContextDto Reviewer(Guid departmentId, Guid? userId = null) => new(
		UserId: userId ?? Guid.NewGuid(),
		IsAuthenticated: true,
		IsEmployee: true,
		HomeDepartmentIds: new[] { departmentId },
		ContributorDepartmentIds: Array.Empty<Guid>(),
		ReviewerDepartmentIds: new[] { departmentId },
		LibrarianDepartmentIds: Array.Empty<Guid>(),
		IsAdmin: false,
		PersonaId: null);

	/// <summary>Convenience: produce a Librarian DTO on the given department.</summary>
	public static SessionContextBuilder.SessionContextDto Librarian(Guid departmentId, Guid? userId = null) => new(
		UserId: userId ?? Guid.NewGuid(),
		IsAuthenticated: true,
		IsEmployee: true,
		HomeDepartmentIds: new[] { departmentId },
		ContributorDepartmentIds: Array.Empty<Guid>(),
		ReviewerDepartmentIds: Array.Empty<Guid>(),
		LibrarianDepartmentIds: new[] { departmentId },
		IsAdmin: false,
		PersonaId: null);

	/// <inheritdoc />
	public Task<SessionContextBuilder.SessionContextDto> ResolveAsync(
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default)
		=> Task.FromResult(Current);
}

/// <summary>In-memory <see cref="IWikiProposalReader"/>. Tests pre-load the collection.</summary>
public sealed class StubWikiProposalReader : IWikiProposalReader
{
	/// <summary>The bag of proposals the reader returns. Mutate before requests.</summary>
	public List<WikiProposedRevision> Items { get; } = new();

	/// <inheritdoc />
	public Task<WikiProposedRevision?> GetAsync(Guid proposalId, CancellationToken cancellationToken = default)
		=> Task.FromResult(Items.FirstOrDefault(p => p.Id == proposalId));

	/// <inheritdoc />
	public Task<IReadOnlyList<WikiProposedRevision>> ListAsync(
		ProposalState? state,
		int limit,
		CancellationToken cancellationToken = default)
	{
		var filtered = state is null
			? Items
			: Items.Where(p => p.State == state.Value).ToList();
		var capped = filtered.Take(limit).ToList();
		return Task.FromResult<IReadOnlyList<WikiProposedRevision>>(capped);
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<WikiProposedRevision>> ListDecidedAsync(
		Guid? decidedBy,
		DateTimeOffset? since,
		int limit,
		CancellationToken cancellationToken = default)
	{
		var filtered = Items
			.Where(p => p.State != ProposalState.Pending)
			.Where(p => decidedBy is null || p.DecidedBy == decidedBy)
			.Where(p => since is null || (p.DecidedAt is not null && p.DecidedAt >= since));
		return Task.FromResult<IReadOnlyList<WikiProposedRevision>>(filtered.Take(limit).ToList());
	}
}

/// <summary>Records calls to <see cref="IWikiProposalWriter"/> and returns scripted outcomes.</summary>
public sealed class StubWikiProposalWriter : IWikiProposalWriter
{
	/// <summary>Proposals created via <see cref="CreateAsync"/>.</summary>
	public List<WikiProposedRevision> Created { get; } = new();

	/// <summary>One call captured per <see cref="DecideAsync"/>.</summary>
	public List<DecideCall> Decisions { get; } = new();

	/// <summary>One call captured per <see cref="BulkRejectAsync"/>.</summary>
	public List<BulkRejectCall> BulkRejections { get; } = new();

	/// <summary>Scripted outcome for the next <see cref="BulkRejectAsync"/> call. Defaults to "everything rejected."</summary>
	public Func<IReadOnlyCollection<Guid>, BulkRejectOutcome> BulkRejectResponse { get; set; }
		= ids => new BulkRejectOutcome(
			Rejected: ids.ToList(),
			Skipped: Array.Empty<Guid>(),
			NotFound: Array.Empty<Guid>());

	/// <summary>Scripted return for accept-path DecideAsync (a freshly-materialized revision id).</summary>
	public Guid? AcceptRevisionId { get; set; } = Guid.NewGuid();

	/// <inheritdoc />
	public Task<Guid> CreateAsync(WikiProposedRevision proposal, CancellationToken cancellationToken = default)
	{
		Created.Add(proposal);
		return Task.FromResult(proposal.Id);
	}

	/// <inheritdoc />
	public Task<Guid?> DecideAsync(
		Guid proposalId,
		ProposalState decision,
		Guid decidedBy,
		string? reason,
		CancellationToken cancellationToken = default)
	{
		Decisions.Add(new DecideCall(proposalId, decision, decidedBy, reason));
		return Task.FromResult(decision == ProposalState.Accepted ? AcceptRevisionId : null);
	}

	/// <inheritdoc />
	public Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult(0);

	/// <inheritdoc />
	public Task<BulkRejectOutcome> BulkRejectAsync(
		IReadOnlyCollection<Guid> proposalIds,
		Guid decidedBy,
		string reason,
		CancellationToken cancellationToken = default)
	{
		BulkRejections.Add(new BulkRejectCall(proposalIds.ToList(), decidedBy, reason));
		return Task.FromResult(BulkRejectResponse(proposalIds));
	}

	/// <summary>One captured DecideAsync call.</summary>
	public sealed record DecideCall(Guid ProposalId, ProposalState Decision, Guid DecidedBy, string? Reason);

	/// <summary>One captured BulkRejectAsync call.</summary>
	public sealed record BulkRejectCall(IReadOnlyList<Guid> ProposalIds, Guid DecidedBy, string Reason);
}

/// <summary>In-memory <see cref="IWikiPageWriter"/>. Records every call.</summary>
public sealed class StubWikiPageWriter : IWikiPageWriter
{
	/// <summary>Captured EnsurePageAsync calls.</summary>
	public List<EnsurePageRequest> EnsureCalls { get; } = new();

	/// <summary>Captured RenameAsync calls.</summary>
	public List<(Guid PageId, string NewTitle)> RenameCalls { get; } = new();

	/// <summary>Captured SetLockedAsync calls.</summary>
	public List<(Guid PageId, bool Locked)> LockCalls { get; } = new();

	/// <summary>Scripted ensure-page response.</summary>
	public Func<EnsurePageRequest, EnsurePageResult> EnsureResponse { get; set; }
		= req => new EnsurePageResult(Guid.NewGuid(), PageCreated: true, FacetCreated: true);

	/// <summary>Whether RenameAsync returns true (default true).</summary>
	public bool RenameReturns { get; set; } = true;

	/// <summary>Whether SetLockedAsync returns true (default true).</summary>
	public bool LockReturns { get; set; } = true;

	/// <summary>If set, RenameAsync throws this. Used to simulate the NullWikiPageWriter 503 path.</summary>
	public Exception? ThrowOnRename { get; set; }

	/// <summary>If set, SetLockedAsync throws this.</summary>
	public Exception? ThrowOnLock { get; set; }

	/// <summary>If set, SoftDeleteAsync throws this.</summary>
	public Exception? ThrowOnSoftDelete { get; set; }

	/// <summary>Captured SoftDeleteAsync calls.</summary>
	public List<Guid> SoftDeleteCalls { get; } = new();

	/// <summary>Whether SoftDeleteAsync returns true (default true).</summary>
	public bool SoftDeleteReturns { get; set; } = true;

	/// <summary>If set, RestoreAsync throws this.</summary>
	public Exception? ThrowOnRestore { get; set; }

	/// <summary>Captured RestoreAsync calls.</summary>
	public List<Guid> RestoreCalls { get; } = new();

	/// <summary>Scripted RestoreAsync response. Default = Restored, no conflict.</summary>
	public Func<Guid, RestorePageResult> RestoreResponder { get; set; }
		= _ => new RestorePageResult(RestorePageOutcome.Restored, ConflictingLivePageId: null);

	/// <inheritdoc />
	public Task<EnsurePageResult> EnsurePageAsync(EnsurePageRequest request, CancellationToken cancellationToken = default)
	{
		EnsureCalls.Add(request);
		return Task.FromResult(EnsureResponse(request));
	}

	/// <inheritdoc />
	public Task<bool> RenameAsync(Guid pageId, string newTitle, CancellationToken cancellationToken = default)
	{
		if (ThrowOnRename is Exception ex)
		{
			throw ex;
		}
		RenameCalls.Add((pageId, newTitle));
		return Task.FromResult(RenameReturns);
	}

	/// <inheritdoc />
	public Task<bool> SetLockedAsync(Guid pageId, bool locked, CancellationToken cancellationToken = default)
	{
		if (ThrowOnLock is Exception ex)
		{
			throw ex;
		}
		LockCalls.Add((pageId, locked));
		return Task.FromResult(LockReturns);
	}

	/// <inheritdoc />
	public Task<bool> SoftDeleteAsync(Guid pageId, CancellationToken cancellationToken = default)
	{
		if (ThrowOnSoftDelete is Exception ex)
		{
			throw ex;
		}
		SoftDeleteCalls.Add(pageId);
		return Task.FromResult(SoftDeleteReturns);
	}

	/// <inheritdoc />
	public Task<RestorePageResult> RestoreAsync(Guid pageId, CancellationToken cancellationToken = default)
	{
		if (ThrowOnRestore is Exception ex)
		{
			throw ex;
		}
		RestoreCalls.Add(pageId);
		return Task.FromResult(RestoreResponder(pageId));
	}
}

/// <summary>Scripted <see cref="global::AiLibrarian.Api.WikiMaintenance.IWikiSourcePoolBuilder"/>.</summary>
public sealed class StubWikiSourcePoolBuilder : global::AiLibrarian.Api.WikiMaintenance.IWikiSourcePoolBuilder
{
	/// <summary>What the next BuildAsync returns. Default empty pool with a recognisable deployment string.</summary>
	public global::AiLibrarian.Api.WikiMaintenance.WikiSourcePoolResult Response { get; set; }
		= new(Array.Empty<global::AiLibrarian.WikiMaintainer.WikiMaintenanceSourceChunk>(), "test-embedding");

	/// <summary>If set, BuildAsync throws this on every call instead of returning <see cref="Response"/>. Useful for the 503 / fallback paths.</summary>
	public Exception? ThrowOnBuild { get; set; }

	/// <inheritdoc />
	public Task<global::AiLibrarian.Api.WikiMaintenance.WikiSourcePoolResult> BuildAsync(
		global::AiLibrarian.Infrastructure.Rls.RlsSessionContext rlsContext,
		string query,
		CancellationToken cancellationToken)
	{
		if (ThrowOnBuild is Exception ex)
		{
			throw ex;
		}
		return Task.FromResult(Response);
	}
}

/// <summary>Scripted <see cref="global::AiLibrarian.Infrastructure.Persistence.IWikiRevisionNumberer"/>.</summary>
public sealed class StubWikiRevisionNumberer : global::AiLibrarian.Infrastructure.Persistence.IWikiRevisionNumberer
{
	/// <summary>What the next NextAsync returns. Default 1.</summary>
	public int Response { get; set; } = 1;

	/// <inheritdoc />
	public Task<int> NextAsync(
		Guid pageId,
		global::AiLibrarian.Domain.Classification classification,
		Guid? personaId,
		CancellationToken cancellationToken)
		=> Task.FromResult(Response);
}

/// <summary>Scripted <see cref="global::AiLibrarian.Infrastructure.Persistence.IDanglingFacetReader"/>.</summary>
public sealed class StubDanglingFacetReader : global::AiLibrarian.Infrastructure.Persistence.IDanglingFacetReader
{
	/// <summary>What the next FindAsync returns. Default empty.</summary>
	public IReadOnlyList<global::AiLibrarian.Infrastructure.Persistence.DanglingFacet> Response { get; set; }
		= Array.Empty<global::AiLibrarian.Infrastructure.Persistence.DanglingFacet>();

	/// <inheritdoc />
	public Task<IReadOnlyList<global::AiLibrarian.Infrastructure.Persistence.DanglingFacet>> FindAsync(
		DateTimeOffset? since,
		Guid? departmentId,
		int maxFacets,
		CancellationToken cancellationToken)
		=> Task.FromResult(Response);
}

/// <summary>Scripted <see cref="global::AiLibrarian.WikiMaintainer.IWikiMaintainer"/>.</summary>
public sealed class StubWikiMaintainer : global::AiLibrarian.WikiMaintainer.IWikiMaintainer
{
	/// <summary>Captured requests.</summary>
	public List<global::AiLibrarian.WikiMaintainer.WikiMaintenanceRequest> Calls { get; } = new();

	/// <summary>Scripted response factory; defaults to a successful 3-claim revision.</summary>
	public Func<global::AiLibrarian.WikiMaintainer.WikiMaintenanceRequest, global::AiLibrarian.WikiMaintainer.WikiMaintenanceResult> Responder { get; set; }
		= req => new global::AiLibrarian.WikiMaintainer.WikiMaintenanceResult(
			Succeeded: true,
			RevisionId: Guid.NewGuid(),
			BodyMarkdown: "Body.",
			ClaimCount: 3,
			CitationCount: 5,
			ValidationResult: new global::AiLibrarian.Domain.Citations.CitationValidationResult(Array.Empty<global::AiLibrarian.Domain.Citations.CitationViolation>()),
			RejectionReason: null);

	/// <inheritdoc />
	public Task<global::AiLibrarian.WikiMaintainer.WikiMaintenanceResult> GenerateRevisionAsync(
		global::AiLibrarian.WikiMaintainer.WikiMaintenanceRequest request,
		CancellationToken cancellationToken = default)
	{
		Calls.Add(request);
		return Task.FromResult(Responder(request));
	}
}

/// <summary>Scripted <see cref="global::AiLibrarian.Domain.Wiki.IWikiPageCandidateGenerator"/>.</summary>
public sealed class StubWikiPageCandidateGenerator : global::AiLibrarian.Domain.Wiki.IWikiPageCandidateGenerator
{
	/// <summary>Captured discovery calls.</summary>
	public List<(Guid Department, int Sample, int Max)> Calls { get; } = new();

	/// <summary>Default response: empty batch with a recognisable embedding-deployment string.</summary>
	public global::AiLibrarian.Domain.Wiki.WikiPageCandidateBatch Response { get; set; }
		= new(Array.Empty<global::AiLibrarian.Domain.Wiki.WikiPageCandidate>(), SampledChunkCount: 0, EmbeddingDeployment: "test-embedding");

	/// <summary>If set, DiscoverAsync throws this. Used to simulate the source-pool-unavailable 503 path.</summary>
	public Exception? ThrowOnDiscover { get; set; }

	/// <inheritdoc />
	public Task<global::AiLibrarian.Domain.Wiki.WikiPageCandidateBatch> DiscoverAsync(
		Guid departmentId,
		int sampleSize,
		int maxCandidates,
		Guid correlationId,
		CancellationToken cancellationToken = default)
	{
		Calls.Add((departmentId, sampleSize, maxCandidates));
		if (ThrowOnDiscover is Exception ex)
		{
			throw ex;
		}
		return Task.FromResult(Response);
	}
}
