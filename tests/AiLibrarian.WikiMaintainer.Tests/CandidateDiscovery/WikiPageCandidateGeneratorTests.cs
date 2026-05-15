using System.Runtime.CompilerServices;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.WikiMaintainer.CandidateDiscovery;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.WikiMaintainer.Tests.CandidateDiscovery;

/// <summary>
/// End-to-end-ish tests for the candidate-discovery orchestrator with
/// stubbed sampler, embedder, chat, and reader. Asserts the contract
/// the API endpoint depends on: empty sample → empty batch, naming
/// failure → candidate dropped, existing-slug dedup works, slug
/// validation rejects bad LLM output, classification ceiling picks the
/// highest in the cluster.
/// </summary>
public sealed class WikiPageCandidateGeneratorTests
{
	private static readonly Guid Dept = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
	private static readonly string[] OneExistingSlug = new[] { "ingest-worker" };
	private static readonly float[] UnitVector = new[] { 1.0f, 0.0f };

	[Fact]
	public async Task DiscoverAsync_Returns_Empty_When_Sample_Is_Empty()
	{
		var generator = Build(
			sampler: new StubSampler(Array.Empty<SampledChunk>()),
			embedder: new StubEmbedder(),
			chat: new StubChat(_ => """{"title":"x","slug":"x","summary":"x"}"""),
			pageReader: new StubReader(Array.Empty<string>()));

		var batch = await generator.DiscoverAsync(Dept, sampleSize: 100, maxCandidates: 5, correlationId: Guid.NewGuid());

		batch.Candidates.Should().BeEmpty();
		batch.SampledChunkCount.Should().Be(0);
	}

	[Fact]
	public async Task DiscoverAsync_Names_Cluster_And_Returns_Candidate()
	{
		var chunks = new[]
		{
			Chunk(content: "chunk-a"),
			Chunk(content: "chunk-b"),
			Chunk(content: "chunk-c"),
		};

		var generator = Build(
			sampler: new StubSampler(chunks),
			embedder: ConstantEmbedder(),
			chat: new StubChat(_ => """{"title":"Ingest Worker","slug":"ingest-worker","summary":"How the worker boots."}"""),
			pageReader: new StubReader(Array.Empty<string>()));

		var batch = await generator.DiscoverAsync(Dept, sampleSize: 100, maxCandidates: 5, correlationId: Guid.NewGuid());

		batch.SampledChunkCount.Should().Be(3);
		batch.Candidates.Should().HaveCount(1);
		batch.Candidates[0].ProposedTitle.Should().Be("Ingest Worker");
		batch.Candidates[0].ProposedSlug.Should().Be("ingest-worker");
		batch.Candidates[0].Summary.Should().Contain("worker boots");
	}

	[Fact]
	public async Task DiscoverAsync_Dedupes_Against_Existing_Slugs()
	{
		var chunks = new[] { Chunk("a"), Chunk("b") };

		var generator = Build(
			sampler: new StubSampler(chunks),
			embedder: ConstantEmbedder(),
			chat: new StubChat(_ => """{"title":"Ingest Worker","slug":"ingest-worker","summary":"text"}"""),
			pageReader: new StubReader(OneExistingSlug));

		var batch = await generator.DiscoverAsync(Dept, sampleSize: 100, maxCandidates: 5, correlationId: Guid.NewGuid());

		batch.Candidates.Should().BeEmpty("the LLM-proposed slug already exists as a wiki page");
	}

	[Fact]
	public async Task DiscoverAsync_Falls_Back_To_Title_Derived_Slug_When_LLM_Slug_Invalid()
	{
		var chunks = new[] { Chunk("a"), Chunk("b") };

		var generator = Build(
			sampler: new StubSampler(chunks),
			embedder: ConstantEmbedder(),
			chat: new StubChat(_ => """{"title":"Ingest Worker","slug":"BAD SLUG WITH SPACES","summary":"x"}"""),
			pageReader: new StubReader(Array.Empty<string>()));

		var batch = await generator.DiscoverAsync(Dept, sampleSize: 100, maxCandidates: 5, correlationId: Guid.NewGuid());

		batch.Candidates.Should().HaveCount(1);
		batch.Candidates[0].ProposedSlug.Should().Be("ingest-worker", "we derive a slug from the title when the LLM's slug is invalid");
	}

	[Fact]
	public async Task DiscoverAsync_Skips_Candidate_When_LLM_Returns_Garbage()
	{
		var chunks = new[] { Chunk("a"), Chunk("b") };

		var generator = Build(
			sampler: new StubSampler(chunks),
			embedder: ConstantEmbedder(),
			chat: new StubChat(_ => "not a json object at all"),
			pageReader: new StubReader(Array.Empty<string>()));

		var batch = await generator.DiscoverAsync(Dept, sampleSize: 100, maxCandidates: 5, correlationId: Guid.NewGuid());

		batch.Candidates.Should().BeEmpty();
	}

	[Fact]
	public async Task DiscoverAsync_Sets_Highest_Classification_From_Cluster_Members()
	{
		// Mixed-classification chunks; ceiling should be the highest.
		var chunks = new[]
		{
			Chunk(content: "a", classification: Classification.Internal),
			Chunk(content: "b", classification: Classification.Confidential),
			Chunk(content: "c", classification: Classification.Public),
		};

		var generator = Build(
			sampler: new StubSampler(chunks),
			embedder: ConstantEmbedder(),
			chat: new StubChat(_ => """{"title":"Test","slug":"test","summary":""}"""),
			pageReader: new StubReader(Array.Empty<string>()));

		var batch = await generator.DiscoverAsync(Dept, sampleSize: 100, maxCandidates: 5, correlationId: Guid.NewGuid());

		batch.Candidates.Should().HaveCount(1);
		batch.Candidates[0].HighestClassification.Should().Be(Classification.Confidential);
	}

	[Fact]
	public async Task DiscoverAsync_Rejects_Empty_DepartmentId()
	{
		var generator = Build(
			new StubSampler(Array.Empty<SampledChunk>()),
			new StubEmbedder(),
			new StubChat(_ => ""),
			new StubReader(Array.Empty<string>()));

		var act = async () => await generator.DiscoverAsync(Guid.Empty, 100, 5, Guid.NewGuid());
		await act.Should().ThrowAsync<ArgumentException>();
	}

	// --- helpers ---

	private static SampledChunk Chunk(string content, Classification classification = Classification.Internal)
		=> new(Guid.NewGuid(), content, classification);

	private static StubEmbedder ConstantEmbedder()
		=> new(); // every input → unit vector [1,0]

	private static WikiPageCandidateGenerator Build(
		IChunkSampler sampler,
		IEmbeddingProvider embedder,
		IChatProvider chat,
		IWikiPageReader pageReader)
		=> new(
			sampler,
			embedder,
			chat,
			pageReader,
			Options.Create(new WikiPageCandidateGeneratorOptions
			{
				EmbeddingDeployment = "test-embed",
				ChatDeployment = "test-chat",
				RepresentativesPerCluster = 3,
				MaxCharsPerChunk = 2048,
				MaxNamingTokens = 200,
				NamingTemperature = 0.2,
			}),
			NullLogger<WikiPageCandidateGenerator>.Instance);

	private sealed class StubSampler : IChunkSampler
	{
		private readonly IReadOnlyList<SampledChunk> _chunks;

		public StubSampler(IReadOnlyList<SampledChunk> chunks) => _chunks = chunks;

		public Task<IReadOnlyList<SampledChunk>> SampleAsync(
			Guid departmentId,
			int limit,
			int maxCharsPerChunk = 4096,
			CancellationToken cancellationToken = default)
			=> Task.FromResult(_chunks);
	}

	private sealed class StubEmbedder : IEmbeddingProvider
	{
		public string ProviderId => "stub";

		public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
			string model,
			IReadOnlyList<string> inputs,
			Guid correlationId,
			CancellationToken cancellationToken = default)
		{
			// Every chunk gets the same unit vector -> k-means produces 1
			// non-empty cluster for any k.
			var vectors = inputs
				.Select(_ => new ReadOnlyMemory<float>(UnitVector))
				.ToList();
			return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(vectors);
		}
	}

	private sealed class StubChat : IChatProvider
	{
		private readonly Func<ChatCompletionRequest, string> _responder;

		public StubChat(Func<ChatCompletionRequest, string> responder) => _responder = responder;

		public string ProviderId => "stub";

#pragma warning disable CS1998 // async-iterator without await
		public async IAsyncEnumerable<ChatCompletionChunk> StreamCompletionAsync(
			ChatCompletionRequest request,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			yield return new ChatCompletionChunk(_responder(request), FinishReason: "stop");
		}
#pragma warning restore CS1998
	}

	private sealed class StubReader : IWikiPageReader
	{
		private readonly HashSet<string> _slugs;

		public StubReader(IReadOnlyCollection<string> slugs)
			=> _slugs = new HashSet<string>(slugs, StringComparer.Ordinal);

		public Task<bool> IsLockedAsync(Guid pageId, CancellationToken cancellationToken = default)
			=> Task.FromResult(false);

		public Task<IReadOnlySet<string>> ListSlugsAsync(Guid departmentId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlySet<string>>(_slugs);
	}
}
