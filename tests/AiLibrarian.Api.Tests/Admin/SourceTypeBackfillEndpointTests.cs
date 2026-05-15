using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

using AiLibrarian.Api.Auth;
using AiLibrarian.Api.Tests.WikiMaintenance;
using AiLibrarian.Domain.Sources;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiLibrarian.Api.Tests.Admin;

/// <summary>
/// Handler-level coverage for
/// <c>POST /api/admin/sources/source-type/backfill</c>. Stubs
/// <see cref="ISourceTypeBackfiller"/> so the endpoint's auth gate,
/// batch-size pass-through, audit emission, and 503 fallback are
/// exercisable without Postgres.
/// </summary>
public sealed class SourceTypeBackfillEndpointTests : IClassFixture<SourceTypeBackfillEndpointTests.Factory>
{
	private readonly Factory _factory;

	public SourceTypeBackfillEndpointTests(Factory factory)
	{
		_factory = factory;
		_factory.Sessions.Current = StubSessionContextResolver.Anonymous();
		_factory.Backfiller.Calls.Clear();
		_factory.Backfiller.ThrowOnNext = null;
		_factory.Backfiller.NextOutcome = new SourceTypeBackfillOutcome(
			ClassifiedThisCall: 0,
			RemainingUnclassified: 0,
			ClassificationCounts: new Dictionary<string, int>(StringComparer.Ordinal));
	}

	[Fact]
	public async Task Backfill_returns_403_for_anonymous_caller()
	{
		using var client = _factory.CreateClient();

		var response = await client.PostAsync(
			new Uri("/api/admin/sources/source-type/backfill", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Backfill_returns_403_for_authenticated_non_admin()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Librarian(Guid.NewGuid());

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri("/api/admin/sources/source-type/backfill", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Backfill_defaults_batch_size_to_100_when_omitted()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri("/api/admin/sources/source-type/backfill", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.Backfiller.Calls.Should().ContainSingle().Which.Should().Be(100);
	}

	[Fact]
	public async Task Backfill_passes_explicit_batch_size_through()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri("/api/admin/sources/source-type/backfill?batchSize=42", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_factory.Backfiller.Calls.Should().ContainSingle().Which.Should().Be(42);
	}

	[Fact]
	public async Task Backfill_returns_outcome_payload()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.Backfiller.NextOutcome = new SourceTypeBackfillOutcome(
			ClassifiedThisCall: 27,
			RemainingUnclassified: 173,
			ClassificationCounts: new Dictionary<string, int>(StringComparer.Ordinal)
			{
				[SourceType.Runbook] = 12,
				[SourceType.Document] = 15,
			});

		using var client = _factory.CreateClient();
		var response = await client.PostAsync(
			new Uri("/api/admin/sources/source-type/backfill", UriKind.Relative),
			content: null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
		payload.GetProperty("classifiedThisCall").GetInt32().Should().Be(27);
		payload.GetProperty("remainingUnclassified").GetInt64().Should().Be(173);
		var counts = payload.GetProperty("classificationCounts");
		counts.GetProperty(SourceType.Runbook).GetInt32().Should().Be(12);
		counts.GetProperty(SourceType.Document).GetInt32().Should().Be(15);
	}

	[Fact]
	public async Task Backfill_returns_503_when_backfiller_unconfigured()
	{
		_factory.Sessions.Current = StubSessionContextResolver.Admin();
		_factory.Backfiller.ThrowOnNext = new InvalidOperationException(
			"Source-type backfill requires ConnectionStrings:Postgres to be configured.");

		try
		{
			using var client = _factory.CreateClient();
			var response = await client.PostAsync(
				new Uri("/api/admin/sources/source-type/backfill", UriKind.Relative),
				content: null);

			response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		}
		finally
		{
			_factory.Backfiller.ThrowOnNext = null;
		}
	}

	// --- factory + stub ---

	public sealed class Factory : WebApplicationFactory<Program>
	{
		public StubSessionContextResolver Sessions { get; } = new();
		public StubSourceTypeBackfiller Backfiller { get; } = new();

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Testing");
			builder.ConfigureServices(services =>
			{
				services.Replace(ServiceDescriptor.Scoped<ISessionContextResolver>(_ => Sessions));
				services.Replace(ServiceDescriptor.Singleton<ISourceTypeBackfiller>(_ => Backfiller));
			});
		}
	}

	public sealed class StubSourceTypeBackfiller : ISourceTypeBackfiller
	{
		public List<int> Calls { get; } = new();
		public Exception? ThrowOnNext { get; set; }
		public SourceTypeBackfillOutcome NextOutcome { get; set; } = new(
			ClassifiedThisCall: 0,
			RemainingUnclassified: 0,
			ClassificationCounts: new Dictionary<string, int>(StringComparer.Ordinal));

		public Task<SourceTypeBackfillOutcome> BackfillBatchAsync(int batchSize, CancellationToken cancellationToken = default)
		{
			Calls.Add(batchSize);
			if (ThrowOnNext is Exception ex)
			{
				throw ex;
			}
			return Task.FromResult(NextOutcome);
		}
	}
}
