using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AiLibrarian.Api.WikiMaintenance;
using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.WikiMaintainer;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Handler-level coverage for <c>POST /api/admin/wiki/maintain</c>.
/// Uses <see cref="WikiMaintenanceWebApplicationFactory"/> to swap in
/// stubs for the source-pool builder, revision numberer, and wiki
/// maintainer so the handler's auth gate, body validation,
/// orchestration flow, and response shape can be exercised without
/// touching Postgres or an LLM provider.
/// </summary>
public sealed class WikiMaintainEndpointTests : IClassFixture<WikiMaintenanceWebApplicationFactory>
{
	private readonly WikiMaintenanceWebApplicationFactory _factory;

	public WikiMaintainEndpointTests(WikiMaintenanceWebApplicationFactory factory)
	{
		_factory = factory;
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous();
		_factory.Maintainer.Calls.Clear();
		_factory.SourcePool.Response = new WikiSourcePoolResult(
			Array.Empty<WikiMaintenanceSourceChunk>(),
			"test-embedding");
		_factory.SourcePool.ThrowOnBuild = null;
		_factory.Numberer.Response = 1;
		// Reset the maintainer's responder to the default success path --
		// previous tests in this class may have overridden it.
		_factory.Maintainer.Responder = _ => new WikiMaintenanceResult(
			Succeeded: true,
			RevisionId: Guid.NewGuid(),
			BodyMarkdown: "Body.",
			ClaimCount: 3,
			CitationCount: 5,
			ValidationResult: new CitationValidationResult(Array.Empty<CitationViolation>()),
			RejectionReason: null);
	}

	[Fact]
	public async Task Maintain_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/maintain", UriKind.Relative),
			new
			{
				pageId = Guid.NewGuid(),
				facetClassification = "Internal",
				topic = "anything",
			});

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Maintain_returns_400_when_pageId_missing()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/maintain", UriKind.Relative),
			new { facetClassification = "Internal", topic = "anything" });

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Maintain_returns_400_when_classification_unparseable()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/maintain", UriKind.Relative),
			new
			{
				pageId = Guid.NewGuid(),
				facetClassification = "NotARealClassification",
				topic = "anything",
			});

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Maintain_returns_400_when_topic_blank()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/maintain", UriKind.Relative),
			new
			{
				pageId = Guid.NewGuid(),
				facetClassification = "Internal",
				topic = "   ",
			});

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Maintain_returns_503_when_source_pool_unavailable()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		// SourcePoolBuilder throws InvalidOperationException -> 503.
		_factory.SourcePool.ThrowOnBuild = new InvalidOperationException("Search:EmbeddingDeployment not set.");
		try
		{
			using var client = _factory.CreateClient();
			var response = await client.PostAsJsonAsync(
				new Uri("/api/admin/wiki/maintain", UriKind.Relative),
				new
				{
					pageId = Guid.NewGuid(),
					facetClassification = "Internal",
					topic = "anything",
				});

			response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		}
		finally
		{
			_factory.SourcePool.ThrowOnBuild = null;
		}
	}

	[Fact]
	public async Task Maintain_success_calls_maintainer_with_expected_request()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var pageId = Guid.NewGuid();
		_factory.Numberer.Response = 7;

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/maintain", UriKind.Relative),
			new
			{
				pageId,
				facetClassification = "Internal",
				topic = "How the ingest worker boots",
			});

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var call = _factory.Maintainer.Calls.Should().ContainSingle().Subject;
		call.PageId.Should().Be(pageId);
		call.FacetClassification.Should().Be(Classification.Internal);
		call.RevisionNumber.Should().Be(7);
		call.Topic.Should().Be("How the ingest worker boots");

		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("succeeded").GetBoolean().Should().BeTrue();
		payload.GetProperty("revisionNumber").GetInt32().Should().Be(7);
	}

	[Fact]
	public async Task Maintain_propagates_rejection_reason_when_maintainer_returns_failure()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.Maintainer.Responder = _ => new WikiMaintenanceResult(
			Succeeded: false,
			RevisionId: null,
			BodyMarkdown: string.Empty,
			ClaimCount: 0,
			CitationCount: 0,
			ValidationResult: new CitationValidationResult(Array.Empty<CitationViolation>()),
			RejectionReason: "R1.ClaimHasCitation@1: missing citation");

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/maintain", UriKind.Relative),
			new
			{
				pageId = Guid.NewGuid(),
				facetClassification = "Internal",
				topic = "anything",
			});

		response.StatusCode.Should().Be(HttpStatusCode.OK,
			"a validation rejection is still a 200 with succeeded=false in the body");
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("succeeded").GetBoolean().Should().BeFalse();
		payload.GetProperty("rejectionReason").GetString().Should().Contain("R1.ClaimHasCitation");
	}

	[Fact]
	public async Task Maintain_threads_persona_id_when_provided()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var personaId = Guid.NewGuid();

		using var client = _factory.CreateClient();
		await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/maintain", UriKind.Relative),
			new
			{
				pageId = Guid.NewGuid(),
				facetClassification = "Internal",
				personaId = personaId.ToString(),
				topic = "anything",
			});

		_factory.Maintainer.Calls.Should().ContainSingle()
			.Which.PersonaId.Should().Be(personaId);
	}
}
