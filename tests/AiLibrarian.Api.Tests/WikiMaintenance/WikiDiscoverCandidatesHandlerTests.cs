using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Wiki;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Handler-level coverage for
/// <c>POST /api/admin/wiki/discover-candidates</c>. Stubs the
/// candidate generator so the handler's auth gate, body validation,
/// default sample-size / max-candidate values, and response shape can
/// be exercised without an LLM provider.
/// </summary>
public sealed class WikiDiscoverCandidatesHandlerTests : IClassFixture<WikiMaintenanceWebApplicationFactory>
{
	private readonly WikiMaintenanceWebApplicationFactory _factory;

	public WikiDiscoverCandidatesHandlerTests(WikiMaintenanceWebApplicationFactory factory)
	{
		_factory = factory;
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous();
		_factory.Candidates.Calls.Clear();
		_factory.Candidates.Response = new WikiPageCandidateBatch(
			Array.Empty<WikiPageCandidate>(),
			SampledChunkCount: 0,
			EmbeddingDeployment: "test-embedding");
	}

	[Fact]
	public async Task DiscoverCandidates_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover-candidates", UriKind.Relative),
			new { departmentId = Guid.NewGuid() });

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task DiscoverCandidates_returns_400_when_departmentId_missing()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover-candidates", UriKind.Relative),
			new { sampleSize = 10 });

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task DiscoverCandidates_defaults_sample_size_and_max_candidates()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var dept = Guid.NewGuid();

		using var client = _factory.CreateClient();
		await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover-candidates", UriKind.Relative),
			new { departmentId = dept });

		var call = _factory.Candidates.Calls.Should().ContainSingle().Subject;
		call.Department.Should().Be(dept);
		call.Sample.Should().Be(100, "default sampleSize");
		call.Max.Should().Be(5, "default maxCandidates");
	}

	[Fact]
	public async Task DiscoverCandidates_passes_through_explicit_sample_and_max()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		var dept = Guid.NewGuid();

		using var client = _factory.CreateClient();
		await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover-candidates", UriKind.Relative),
			new { departmentId = dept, sampleSize = 50, maxCandidates = 3 });

		var call = _factory.Candidates.Calls.Should().ContainSingle().Subject;
		call.Sample.Should().Be(50);
		call.Max.Should().Be(3);
	}

	[Fact]
	public async Task DiscoverCandidates_returns_candidates_in_response_body()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.Candidates.Response = new WikiPageCandidateBatch(
			Candidates: new[]
			{
				new WikiPageCandidate(
					ProposedTitle: "Ingest Worker",
					ProposedSlug: "ingest-worker",
					Summary: "How the worker boots.",
					HighestClassification: Classification.Internal,
					SupportingChunkIds: new[] { Guid.NewGuid(), Guid.NewGuid() },
					ClusterSize: 12),
				new WikiPageCandidate(
					ProposedTitle: "RLS Policies",
					ProposedSlug: "rls-policies",
					Summary: "How visibility is enforced.",
					HighestClassification: Classification.Confidential,
					SupportingChunkIds: new[] { Guid.NewGuid() },
					ClusterSize: 5),
			},
			SampledChunkCount: 100,
			EmbeddingDeployment: "text-embedding-3-large");

		using var client = _factory.CreateClient();
		var response = await client.PostAsJsonAsync(
			new Uri("/api/admin/wiki/discover-candidates", UriKind.Relative),
			new { departmentId = Guid.NewGuid() });

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("sampledChunkCount").GetInt32().Should().Be(100);
		payload.GetProperty("embeddingDeployment").GetString().Should().Be("text-embedding-3-large");
		var candidates = payload.GetProperty("candidates");
		candidates.GetArrayLength().Should().Be(2);
		candidates[0].GetProperty("proposedSlug").GetString().Should().Be("ingest-worker");
		candidates[0].GetProperty("clusterSize").GetInt32().Should().Be(12);
		candidates[1].GetProperty("highestClassification").GetString().Should().Be("Confidential");
	}

	[Fact]
	public async Task DiscoverCandidates_returns_503_when_generator_throws_InvalidOperation()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.Candidates.Response = null!; // unused; we'll set a throw instead
		_factory.Candidates.ThrowOnDiscover = new InvalidOperationException("Search:EmbeddingDeployment not set.");

		try
		{
			using var client = _factory.CreateClient();
			var response = await client.PostAsJsonAsync(
				new Uri("/api/admin/wiki/discover-candidates", UriKind.Relative),
				new { departmentId = Guid.NewGuid() });

			response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		}
		finally
		{
			_factory.Candidates.ThrowOnDiscover = null;
			_factory.Candidates.Response = new WikiPageCandidateBatch(
				Array.Empty<WikiPageCandidate>(),
				SampledChunkCount: 0,
				EmbeddingDeployment: "test-embedding");
		}
	}
}
