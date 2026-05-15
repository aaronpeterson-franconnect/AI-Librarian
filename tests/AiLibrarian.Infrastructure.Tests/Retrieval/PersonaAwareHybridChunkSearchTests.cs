using AiLibrarian.Domain;
using AiLibrarian.Domain.Personas;
using AiLibrarian.Infrastructure.Retrieval;
using AiLibrarian.Infrastructure.Rls;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Infrastructure.Tests.Retrieval;

/// <summary>
/// Unit coverage for the persona-aware decorator. Uses stubs for the
/// inner hybrid search and the profile reader so the decorator's
/// orchestration logic (delegate vs over-fetch-and-rerank, fallback
/// on profile-load failure, limit truncation) is provable without
/// touching Postgres.
/// </summary>
public sealed class PersonaAwareHybridChunkSearchTests
{
	private static readonly Guid HomeDept = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
	private static readonly Guid EngineeringPersona = Guid.Parse("99999999-1111-1111-1111-111111111111");
	private static readonly ReadOnlyMemory<float> AnyEmbedding = new(new float[] { 0.1f, 0.2f });

	[Fact]
	public async Task No_persona_set_delegates_to_inner_unchanged()
	{
		// Inner returns three hits with a specific order; with no
		// persona on the session, the decorator must return them
		// verbatim (no rerank, no over-fetch).
		var hits = new[]
		{
			Hit(score: 0.9, dept: HomeDept),
			Hit(score: 0.7, dept: HomeDept),
			Hit(score: 0.5, dept: HomeDept),
		};
		var inner = new StubInner(_ => hits);
		var decorator = Build(inner, new StubProfileReader(_ => PersonaRetrievalProfile.Neutral));

		var result = await decorator.SearchAsync(
			ContextNoPersona(),
			"query",
			AnyEmbedding,
			new HybridSearchRequestOptions(Limit: 3, VectorWeight: 0.6),
			CancellationToken.None);

		inner.Calls.Should().HaveCount(1, "no over-fetch when persona is null");
		inner.Calls[0].Limit.Should().Be(3);
		result.Should().BeEquivalentTo(hits);
	}

	[Fact]
	public async Task Persona_set_over_fetches_and_reranks()
	{
		// Inner returns 6 hits when asked. With limit=2 and overFetch=3,
		// decorator should ask for 6; profile reranks; output is top 2.
		var hits = Enumerable.Range(0, 6)
			.Select(i => Hit(score: 0.9 - (i * 0.1), dept: i == 5 ? HomeDept : Guid.NewGuid()))
			.ToArray();
		var inner = new StubInner(_ => hits);

		// Strong same-dept boost: the i=5 hit (lowest raw score but home dept)
		// should leapfrog the others.
		var profile = new PersonaRetrievalProfile(
			SourceTypeWeights: new Dictionary<string, double>(),
			RecencyHalfLifeDays: null,
			AuthorityBias: new Dictionary<string, double>(),
			CrossDepartmentBoost: new PersonaCrossDepartmentBoost(SameDepartment: 10.0, CrossDepartmentInternal: 1.0, CrossDepartmentShared: 1.0),
			FloorClassification: null);
		var decorator = Build(inner, new StubProfileReader(_ => profile), overFetchFactor: 3);

		var result = await decorator.SearchAsync(
			ContextWithPersona(),
			"query",
			AnyEmbedding,
			new HybridSearchRequestOptions(Limit: 2, VectorWeight: 0.6),
			CancellationToken.None);

		inner.Calls.Should().HaveCount(1);
		inner.Calls[0].Limit.Should().Be(6, "over-fetch factor 3 × limit 2 = 6");
		result.Should().HaveCount(2);
		result[0].SourceDepartmentId.Should().Be(HomeDept, "same-dept hit gets the 10x boost and ranks first");
	}

	[Fact]
	public async Task Persona_set_with_neutral_profile_preserves_inner_order_truncated()
	{
		var hits = Enumerable.Range(0, 5)
			.Select(i => Hit(score: 0.9 - (i * 0.1), dept: HomeDept))
			.ToArray();
		var inner = new StubInner(_ => hits);
		var decorator = Build(inner, new StubProfileReader(_ => PersonaRetrievalProfile.Neutral));

		var result = await decorator.SearchAsync(
			ContextWithPersona(),
			"query",
			AnyEmbedding,
			new HybridSearchRequestOptions(Limit: 3, VectorWeight: 0.6),
			CancellationToken.None);

		result.Select(h => h.HybridScore).Should().Equal(0.9, 0.8, 0.7);
	}

	[Fact]
	public async Task Profile_reader_failure_falls_back_to_neutral_rerank()
	{
		var hits = Enumerable.Range(0, 4)
			.Select(i => Hit(score: 0.9 - (i * 0.1), dept: HomeDept))
			.ToArray();
		var inner = new StubInner(_ => hits);
		var decorator = Build(
			inner,
			new StubProfileReader(_ => throw new InvalidOperationException("simulated profile outage")));

		var result = await decorator.SearchAsync(
			ContextWithPersona(),
			"query",
			AnyEmbedding,
			new HybridSearchRequestOptions(Limit: 2, VectorWeight: 0.6),
			CancellationToken.None);

		// Falls back to neutral profile → top-N by raw score.
		result.Select(h => h.HybridScore).Should().Equal(0.9, 0.8);
	}

	[Fact]
	public async Task OverFetch_factor_clamped_to_one_at_minimum()
	{
		var inner = new StubInner(_ => Array.Empty<HybridChunkHit>());
		var decorator = Build(inner, new StubProfileReader(_ => PersonaRetrievalProfile.Neutral), overFetchFactor: 0);

		await decorator.SearchAsync(
			ContextWithPersona(),
			"q",
			AnyEmbedding,
			new HybridSearchRequestOptions(Limit: 5, VectorWeight: 0.6),
			CancellationToken.None);

		inner.Calls[0].Limit.Should().BeGreaterThanOrEqualTo(5, "over-fetch factor 0 clamps up to 1, so inner limit >= requested");
	}

	[Fact]
	public async Task OverFetch_capped_by_max_over_fetch_limit()
	{
		var inner = new StubInner(_ => Array.Empty<HybridChunkHit>());
		// requested limit 100, factor 10 -> 1000; cap is 150 -> 150.
		var decorator = Build(inner, new StubProfileReader(_ => PersonaRetrievalProfile.Neutral),
			overFetchFactor: 10, maxOverFetch: 150);

		await decorator.SearchAsync(
			ContextWithPersona(),
			"q",
			AnyEmbedding,
			new HybridSearchRequestOptions(Limit: 100, VectorWeight: 0.6),
			CancellationToken.None);

		inner.Calls[0].Limit.Should().Be(150);
	}

	// --- helpers ---

	private static HybridChunkHit Hit(double score, Guid dept)
		=> new(
			ChunkId: Guid.NewGuid(),
			SourceId: Guid.NewGuid(),
			OrderIndex: 0,
			Excerpt: "excerpt",
			HybridScore: score,
			CosineDistance: 0.1,
			TextRank: 0.5,
			SourceClassification: Classification.Internal,
			SourceDepartmentId: dept,
			SourceCreatedAt: null,
			SourceApprovedAt: null);

	private static RlsSessionContext ContextNoPersona()
		=> RlsSessionContext.Anonymous() with
		{
			UserId = Guid.NewGuid(),
			IsAuthenticated = true,
			IsEmployee = true,
			HomeDepartmentIds = new[] { HomeDept },
		};

	private static RlsSessionContext ContextWithPersona()
		=> ContextNoPersona() with { PersonaId = EngineeringPersona };

	private static PersonaAwareHybridChunkSearch Build(
		IHybridChunkSearch inner,
		IPersonaProfileReader reader,
		int overFetchFactor = 3,
		int maxOverFetch = 150)
		=> new(
			inner,
			reader,
			Options.Create(new PersonaRetrievalOptions
			{
				OverFetchFactor = overFetchFactor,
				MaxOverFetchLimit = maxOverFetch,
			}),
			NullLogger<PersonaAwareHybridChunkSearch>.Instance,
			TimeProvider.System);

	private sealed class StubInner : IHybridChunkSearch
	{
		private readonly Func<HybridSearchRequestOptions, IReadOnlyList<HybridChunkHit>> _responder;
		public List<HybridSearchRequestOptions> Calls { get; } = new();

		public StubInner(Func<HybridSearchRequestOptions, IReadOnlyList<HybridChunkHit>> responder)
			=> _responder = responder;

		public Task<IReadOnlyList<HybridChunkHit>> SearchAsync(
			RlsSessionContext sessionContext,
			string queryText,
			ReadOnlyMemory<float> queryEmbedding,
			HybridSearchRequestOptions options,
			CancellationToken cancellationToken)
		{
			Calls.Add(options);
			return Task.FromResult(_responder(options));
		}
	}

	private sealed class StubProfileReader : IPersonaProfileReader
	{
		private readonly Func<Guid, PersonaRetrievalProfile> _responder;

		public StubProfileReader(Func<Guid, PersonaRetrievalProfile> responder) => _responder = responder;

		public Task<PersonaSynthesisStyle> GetSynthesisStyleAsync(Guid personaId, CancellationToken cancellationToken = default)
			=> Task.FromResult(PersonaSynthesisStyle.Neutral);

		public Task<PersonaRetrievalProfile> GetRetrievalProfileAsync(Guid personaId, CancellationToken cancellationToken = default)
			=> Task.FromResult(_responder(personaId));
	}
}
