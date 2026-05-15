using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Handler-level coverage for the wiki proposal endpoints
/// (<c>/api/admin/wiki/proposed*</c>). Uses
/// <see cref="WikiMaintenanceWebApplicationFactory"/> to swap in
/// in-memory stubs for the proposal reader/writer + a settable
/// session resolver so each test declares the caller's role.
///
/// <para>Covers the role gates (Admin / Reviewer / Librarian /
/// anonymous), the body validations (400 on missing fields / oversized
/// batches), and the success shapes that the response contracts
/// promise.</para>
/// </summary>
public sealed class WikiProposalEndpointTests : IClassFixture<WikiMaintenanceWebApplicationFactory>
{
	private readonly WikiMaintenanceWebApplicationFactory _factory;

	public WikiProposalEndpointTests(WikiMaintenanceWebApplicationFactory factory)
	{
		_factory = factory;
		// Reset stub state between tests -- IClassFixture shares the
		// factory across every test in the class.
		_factory.Proposals.Items.Clear();
		_factory.ProposalWrites.Created.Clear();
		_factory.ProposalWrites.Decisions.Clear();
		_factory.ProposalWrites.BulkRejections.Clear();
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous();
	}

	// ----- list (queue view) -----

	[Fact]
	public async Task List_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(new Uri("/api/admin/wiki/proposed", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task List_returns_filtered_pending_proposals_for_reviewer()
	{
		var dept = Guid.NewGuid();
		_factory.Sessions.Current = StubSessionContextResolver.Reviewer(dept);
		_factory.Proposals.Items.AddRange(new[]
		{
			MakeProposal(state: ProposalState.Pending),
			MakeProposal(state: ProposalState.Pending),
			MakeProposal(state: ProposalState.Rejected),
		});

		using var client = _factory.CreateClient();
		var response = await client.GetAsync(new Uri("/api/admin/wiki/proposed?state=pending", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("items").GetArrayLength().Should().Be(2);
	}

	[Fact]
	public async Task List_returns_all_when_state_omitted()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.Proposals.Items.AddRange(new[]
		{
			MakeProposal(state: ProposalState.Pending),
			MakeProposal(state: ProposalState.Accepted),
			MakeProposal(state: ProposalState.Rejected),
			MakeProposal(state: ProposalState.Expired),
		});

		using var client = _factory.CreateClient();
		var response = await client.GetAsync(new Uri("/api/admin/wiki/proposed", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("items").GetArrayLength().Should().Be(4);
	}

	// ----- accept -----

	[Fact]
	public async Task Accept_returns_404_when_proposal_missing()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var unknown = Guid.NewGuid();

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri($"/api/admin/wiki/proposed/{unknown}/accept", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Accept_transitions_pending_proposal_and_emits_decision()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var proposal = MakeProposal(state: ProposalState.Pending);
		_factory.Proposals.Items.Add(proposal);

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri($"/api/admin/wiki/proposed/{proposal.Id}/accept", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.ProposalWrites.Decisions.Should().ContainSingle()
			.Which.Decision.Should().Be(ProposalState.Accepted);

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("decision").GetString().Should().Be("accepted");
		payload.GetProperty("newRevisionId").GetString().Should().NotBeNullOrEmpty();
	}

	// ----- reject -----

	[Fact]
	public async Task Reject_records_reason_on_writer_call()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var proposal = MakeProposal(state: ProposalState.Pending);
		_factory.Proposals.Items.Add(proposal);

		using var client = _factory.CreateClient();
		var body = new { reason = "Source X retired" };
		var response = await client.PostAsJsonAsync(
			new Uri($"/api/admin/wiki/proposed/{proposal.Id}/reject", UriKind.Relative),
			body);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var decision = _factory.ProposalWrites.Decisions.Should().ContainSingle().Subject;
		decision.Decision.Should().Be(ProposalState.Rejected);
		decision.Reason.Should().Be("Source X retired");
	}

	[Fact]
	public async Task Reject_falls_back_to_default_reason_when_body_missing()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var proposal = MakeProposal(state: ProposalState.Pending);
		_factory.Proposals.Items.Add(proposal);

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri($"/api/admin/wiki/proposed/{proposal.Id}/reject", UriKind.Relative),
			new { });

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.ProposalWrites.Decisions.Should().ContainSingle()
			.Which.Reason.Should().Be("rejected without reason");
	}

	// ----- decision history -----

	[Fact]
	public async Task Decisions_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.GetAsync(
			new Uri("/api/admin/wiki/proposed/decisions", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Decisions_defaults_non_admin_to_own_history()
	{
		var dept = Guid.NewGuid();
		var librarianId = Guid.NewGuid();
		_factory.Sessions.Current = StubSessionContextResolver.Librarian(dept, librarianId);

		// Two decisions: one by this librarian, one by someone else.
		_factory.Proposals.Items.AddRange(new[]
		{
			MakeProposal(state: ProposalState.Rejected, decidedBy: librarianId),
			MakeProposal(state: ProposalState.Rejected, decidedBy: Guid.NewGuid()),
		});

		using var client = _factory.CreateClient();
		var response = await client.GetAsync(
			new Uri("/api/admin/wiki/proposed/decisions", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("items").GetArrayLength().Should().Be(1,
			"non-Admin callers default to filtering by their own user id");
	}

	[Fact]
	public async Task Decisions_blocks_non_admin_from_querying_other_users()
	{
		var dept = Guid.NewGuid();
		var librarianId = Guid.NewGuid();
		var otherUserId = Guid.NewGuid();
		_factory.Sessions.Current = StubSessionContextResolver.Librarian(dept, librarianId);

		using var client = _factory.CreateClient();
		var response = await client.GetAsync(
			new Uri($"/api/admin/wiki/proposed/decisions?decidedBy={otherUserId}", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Decisions_admin_can_query_any_user()
	{
		var someoneElse = Guid.NewGuid();
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.Proposals.Items.Add(MakeProposal(state: ProposalState.Rejected, decidedBy: someoneElse));

		using var client = _factory.CreateClient();
		var response = await client.GetAsync(
			new Uri($"/api/admin/wiki/proposed/decisions?decidedBy={someoneElse}", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("items").GetArrayLength().Should().Be(1);
	}

	[Fact]
	public async Task Decisions_rejects_malformed_decided_by()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.GetAsync(
			new Uri("/api/admin/wiki/proposed/decisions?decidedBy=not-a-guid", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Decisions_rejects_malformed_since()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.GetAsync(
			new Uri("/api/admin/wiki/proposed/decisions?since=not-a-date", UriKind.Relative));

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	// ----- bulk reject -----

	[Fact]
	public async Task BulkReject_returns_401_for_unauthenticated_caller()
	{
		// Anonymous (default) — IsAuthenticated=false.
		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/proposed/bulk-reject", UriKind.Relative),
			new { proposalIds = new[] { Guid.NewGuid() }, reason = "test" });

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task BulkReject_returns_403_for_authenticated_employee_with_no_role()
	{
		// Authenticated but no Reviewer/Librarian/Admin -> 403.
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous() with { UserId = Guid.NewGuid(), IsAuthenticated = true, IsEmployee = true };

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/proposed/bulk-reject", UriKind.Relative),
			new { proposalIds = new[] { Guid.NewGuid() }, reason = "test" });

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task BulkReject_validates_required_fields()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		using var client = _factory.CreateClient();

		// Missing proposalIds.
		var noIds = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/proposed/bulk-reject", UriKind.Relative),
			new { reason = "no ids" });
		noIds.StatusCode.Should().Be(HttpStatusCode.BadRequest);

		// Empty proposalIds.
		var emptyIds = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/proposed/bulk-reject", UriKind.Relative),
			new { proposalIds = Array.Empty<Guid>(), reason = "empty" });
		emptyIds.StatusCode.Should().Be(HttpStatusCode.BadRequest);

		// Missing reason.
		var noReason = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/proposed/bulk-reject", UriKind.Relative),
			new { proposalIds = new[] { Guid.NewGuid() } });
		noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task BulkReject_rejects_oversized_batch()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray();

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/proposed/bulk-reject", UriKind.Relative),
			new { proposalIds = ids, reason = "too many" });

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task BulkReject_forwards_outcome_from_writer()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var rejected = new[] { Guid.NewGuid(), Guid.NewGuid() };
		var skipped = new[] { Guid.NewGuid() };
		var notFound = new[] { Guid.NewGuid() };
		var ids = rejected.Concat(skipped).Concat(notFound).ToArray();

		_factory.ProposalWrites.BulkRejectResponse = _ =>
			new BulkRejectOutcome(rejected, skipped, notFound);

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/proposed/bulk-reject", UriKind.Relative),
			new { proposalIds = ids, reason = "Source X retired" });

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("rejected").GetArrayLength().Should().Be(2);
		payload.GetProperty("skipped").GetArrayLength().Should().Be(1);
		payload.GetProperty("notFound").GetArrayLength().Should().Be(1);

		var call = _factory.ProposalWrites.BulkRejections.Should().ContainSingle().Subject;
		call.ProposalIds.Should().HaveCount(4);
		call.Reason.Should().Be("Source X retired");
	}

	// ----- helpers -----

	private static WikiProposedRevision MakeProposal(
		ProposalState state,
		Guid? decidedBy = null)
	{
		var now = DateTimeOffset.UtcNow;
		var isPending = state == ProposalState.Pending;
		return new WikiProposedRevision(
			Id: Guid.NewGuid(),
			PageId: Guid.NewGuid(),
			MinClassification: Classification.Internal,
			PersonaId: null,
			ProposedRevisionNumber: 1,
			AuthoredBy: Guid.NewGuid(),
			AuthoredAt: now.AddMinutes(-10),
			ExpiresAt: now.AddDays(14),
			BodyMarkdown: "Body.",
			Payload: new WikiProposalPayload(new[]
			{
				new WikiClaimDraft(
					ClaimText: "A claim.",
					Position: 0,
					Citations: new[] { new Citation(Guid.NewGuid(), Guid.NewGuid(), 0, 50, 0.9) }),
			}),
			State: state,
			DecidedBy: isPending ? null : decidedBy ?? Guid.NewGuid(),
			DecidedAt: isPending ? null : now.AddMinutes(-1),
			DecisionReason: isPending ? null : "test");
	}
}
