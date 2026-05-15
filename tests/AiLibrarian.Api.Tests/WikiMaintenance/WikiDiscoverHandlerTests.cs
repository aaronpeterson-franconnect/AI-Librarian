using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AiLibrarian.Domain.Wiki;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Handler-level coverage for <c>POST /api/admin/wiki/discover</c>.
/// The endpoint composes <see cref="IWikiPageWriter.EnsurePageAsync"/>
/// (idempotent page + facet ensure) with the maintain flow. Tests
/// here exercise the auth gate, slug derivation, body validation, and
/// the "create then maintain" composition.
/// </summary>
public sealed class WikiDiscoverHandlerTests : IClassFixture<WikiMaintenanceWebApplicationFactory>
{
	private readonly WikiMaintenanceWebApplicationFactory _factory;

	public WikiDiscoverHandlerTests(WikiMaintenanceWebApplicationFactory factory)
	{
		_factory = factory;
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous();
		_factory.PageWrites.EnsureCalls.Clear();
		_factory.Maintainer.Calls.Clear();
		_factory.PageWrites.EnsureResponse = req => new EnsurePageResult(Guid.NewGuid(), PageCreated: true, FacetCreated: true);
	}

	[Fact]
	public async Task Discover_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new
			{
				departmentId = Guid.NewGuid(),
				title = "Ingest Worker",
				facetClassification = "Internal",
				topic = "How the worker boots",
			});

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Discover_validates_required_fields()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		using var client = _factory.CreateClient();

		var missingDept = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new { title = "x", facetClassification = "Internal", topic = "anything" });
		missingDept.StatusCode.Should().Be(HttpStatusCode.BadRequest);

		var missingTitle = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new { departmentId = Guid.NewGuid(), facetClassification = "Internal", topic = "anything" });
		missingTitle.StatusCode.Should().Be(HttpStatusCode.BadRequest);

		var badClassification = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new
			{
				departmentId = Guid.NewGuid(),
				title = "x",
				facetClassification = "Bogus",
				topic = "anything",
			});
		badClassification.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Discover_derives_slug_from_title_when_slug_omitted()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new
			{
				departmentId = Guid.NewGuid(),
				title = "Ingest Worker Architecture",
				facetClassification = "Internal",
				topic = "anything",
			});

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var call = _factory.PageWrites.EnsureCalls.Should().ContainSingle().Subject;
		call.Slug.Should().Be("ingest-worker-architecture");
	}

	[Fact]
	public async Task Discover_uses_explicit_slug_when_supplied_and_valid()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new
			{
				departmentId = Guid.NewGuid(),
				title = "Some Page",
				slug = "different-slug",
				facetClassification = "Internal",
				topic = "anything",
			});

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.PageWrites.EnsureCalls.Should().ContainSingle()
			.Which.Slug.Should().Be("different-slug");
	}

	[Fact]
	public async Task Discover_rejects_explicit_slug_that_violates_constraint()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new
			{
				departmentId = Guid.NewGuid(),
				title = "Some Page",
				slug = "BAD SLUG WITH SPACES",
				facetClassification = "Internal",
				topic = "anything",
			});

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Discover_returns_400_when_title_yields_empty_slug()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		// Title is all punctuation -> WikiSlug.From returns null -> 400.
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new
			{
				departmentId = Guid.NewGuid(),
				title = "!!!",
				facetClassification = "Internal",
				topic = "anything",
			});

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Discover_returns_503_when_page_writer_unconfigured()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.PageWrites.EnsureResponse = _ => throw new InvalidOperationException("Wiki page writes require ConnectionStrings:Postgres.");
		try
		{
			using var client = _factory.CreateClient();
			var response = await client.PostAsJsonAsync(
				new Uri("/api/admin/wiki/discover", UriKind.Relative),
				new
				{
					departmentId = Guid.NewGuid(),
					title = "Page",
					facetClassification = "Internal",
					topic = "anything",
				});

			response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		}
		finally
		{
			_factory.PageWrites.EnsureResponse = req => new EnsurePageResult(Guid.NewGuid(), PageCreated: true, FacetCreated: true);
		}
	}

	[Fact]
	public async Task Discover_response_carries_page_and_facet_created_flags()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var pageId = Guid.NewGuid();
		_factory.PageWrites.EnsureResponse = _ => new EnsurePageResult(pageId, PageCreated: false, FacetCreated: true);

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new
			{
				departmentId = Guid.NewGuid(),
				title = "Existing Page",
				facetClassification = "Confidential",
				topic = "anything",
			});

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("pageId").GetString().Should().Be(pageId.ToString());
		payload.GetProperty("pageCreated").GetBoolean().Should().BeFalse();
		payload.GetProperty("facetCreated").GetBoolean().Should().BeTrue();
		payload.GetProperty("maintenance").GetProperty("succeeded").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Discover_chains_maintainer_call_with_resolved_pageId()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var pageId = Guid.NewGuid();
		_factory.PageWrites.EnsureResponse = _ => new EnsurePageResult(pageId, PageCreated: true, FacetCreated: true);

		using var client = _factory.CreateClient();
		await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover", UriKind.Relative),
			new
			{
				departmentId = Guid.NewGuid(),
				title = "Test Page",
				facetClassification = "Internal",
				topic = "anything",
			});

		_factory.Maintainer.Calls.Should().ContainSingle()
			.Which.PageId.Should().Be(pageId,
				"the maintain call must use the page id the ensure-page step returned");
	}
}
