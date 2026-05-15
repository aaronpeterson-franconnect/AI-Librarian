using AiLibrarian.Api.WikiMaintenance;
using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Infrastructure.Retrieval;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.LlmGateway;
using AiLibrarian.LlmGateway.Abstractions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.Tests.WikiMaintenance;

/// <summary>
/// Stub-driven unit tests for the post-retrieval chunk-content upgrade
/// in <see cref="WikiSourcePoolBuilder"/>. The hybrid search returns
/// 600-char excerpts; the builder is supposed to swap in full canonical
/// content via <see cref="IChunkContentReader"/>, falling back to the
/// excerpt when the reader misses or throws.
/// </summary>
public sealed class WikiSourcePoolBuilderTests
{
	private const string TestDeployment = "embed-test";
	private const int Dim = 1536;

	private static readonly Guid ChunkA = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid ChunkB = Guid.Parse("22222222-2222-2222-2222-222222222222");

	[Fact]
	public async Task BuildAsync_Replaces_Excerpt_With_Full_Content()
	{
		var hits = new[]
		{
			Hit(ChunkA, excerpt: "excerpt-a (600 chars)"),
			Hit(ChunkB, excerpt: "excerpt-b (600 chars)"),
		};

		var fullContent = new Dictionary<Guid, string>
		{
			[ChunkA] = "FULL-A: complete canonical markdown well beyond 600 chars",
			[ChunkB] = "FULL-B: complete canonical markdown well beyond 600 chars",
		};

		var builder = CreateBuilder(hits, new StubReader(fullContent), maxChars: 4096);

		var result = await builder.BuildAsync(RlsSessionContext.System(), "query", CancellationToken.None);

		result.EmbeddingDeployment.Should().Be(TestDeployment);
		result.Chunks.Should().HaveCount(2);
		result.Chunks[0].ChunkId.Should().Be(ChunkA);
		result.Chunks[0].ContentMarkdown.Should().StartWith("FULL-A:");
		result.Chunks[1].ChunkId.Should().Be(ChunkB);
		result.Chunks[1].ContentMarkdown.Should().StartWith("FULL-B:");
	}

	[Fact]
	public async Task BuildAsync_Falls_Back_To_Excerpt_When_Reader_Misses()
	{
		// Reader returns content for A only; B should fall through to its excerpt.
		var hits = new[]
		{
			Hit(ChunkA, excerpt: "excerpt-a"),
			Hit(ChunkB, excerpt: "excerpt-b"),
		};

		var fullContent = new Dictionary<Guid, string>
		{
			[ChunkA] = "FULL-A canonical markdown",
		};

		var builder = CreateBuilder(hits, new StubReader(fullContent), maxChars: 4096);

		var result = await builder.BuildAsync(RlsSessionContext.System(), "query", CancellationToken.None);

		result.Chunks[0].ContentMarkdown.Should().Be("FULL-A canonical markdown");
		result.Chunks[1].ContentMarkdown.Should().Be("excerpt-b");
	}

	[Fact]
	public async Task BuildAsync_Falls_Back_To_Excerpt_When_Reader_Throws()
	{
		var hits = new[] { Hit(ChunkA, excerpt: "excerpt-a") };

		var builder = CreateBuilder(hits, new ThrowingReader(), maxChars: 4096);

		var result = await builder.BuildAsync(RlsSessionContext.System(), "query", CancellationToken.None);

		// One hit, no full-content; falls through to the excerpt for every row.
		result.Chunks.Should().HaveCount(1);
		result.Chunks[0].ContentMarkdown.Should().Be("excerpt-a");
	}

	[Fact]
	public async Task BuildAsync_Skips_Upgrade_When_Cap_Is_Disabled()
	{
		// Cap <= 600 means "don't bother fetching full content". The
		// reader must not be invoked at all.
		var hits = new[] { Hit(ChunkA, excerpt: "raw-excerpt") };

		var reader = new RecordingReader();
		var builder = CreateBuilder(hits, reader, maxChars: 600);

		var result = await builder.BuildAsync(RlsSessionContext.System(), "query", CancellationToken.None);

		reader.CallCount.Should().Be(0);
		result.Chunks[0].ContentMarkdown.Should().Be("raw-excerpt");
	}

	[Fact]
	public async Task BuildAsync_Skips_Upgrade_When_No_Hits()
	{
		var reader = new RecordingReader();
		var builder = CreateBuilder(Array.Empty<HybridChunkHit>(), reader, maxChars: 4096);

		var result = await builder.BuildAsync(RlsSessionContext.System(), "query", CancellationToken.None);

		reader.CallCount.Should().Be(0);
		result.Chunks.Should().BeEmpty();
	}

	// --- helpers ---

	private static HybridChunkHit Hit(Guid chunkId, string excerpt)
		=> new(
			ChunkId: chunkId,
			SourceId: Guid.NewGuid(),
			OrderIndex: 0,
			Excerpt: excerpt,
			HybridScore: 1.0,
			CosineDistance: 0.1,
			TextRank: 0.5,
			SourceClassification: Classification.Internal,
			SourceDepartmentId: Guid.NewGuid());

	private static WikiSourcePoolBuilder CreateBuilder(
		IReadOnlyList<HybridChunkHit> hits,
		IChunkContentReader reader,
		int maxChars)
	{
		var hybrid = new StubHybrid(hits);
		var embeddings = new StubEmbeddings();

		var searchOpts = Options.Create(new SearchOptions
		{
			EmbeddingDeployment = TestDeployment,
			ExpectedEmbeddingDimensions = Dim,
		});
		var llmOpts = Options.Create(new LlmGatewayOptions());
		var maintenanceOpts = Options.Create(new WikiMaintenanceOptions
		{
			RetrievalLimit = 20,
			HybridVectorWeight = 0.6,
			MaxChunkContentChars = maxChars,
		});

		return new WikiSourcePoolBuilder(
			hybrid,
			embeddings,
			reader,
			llmOpts,
			searchOpts,
			maintenanceOpts,
			NullLogger<WikiSourcePoolBuilder>.Instance);
	}

	private sealed class StubHybrid : IHybridChunkSearch
	{
		private readonly IReadOnlyList<HybridChunkHit> _hits;

		public StubHybrid(IReadOnlyList<HybridChunkHit> hits) => _hits = hits;

		public Task<IReadOnlyList<HybridChunkHit>> SearchAsync(
			RlsSessionContext sessionContext,
			string queryText,
			ReadOnlyMemory<float> queryEmbedding,
			HybridSearchRequestOptions options,
			CancellationToken cancellationToken)
			=> Task.FromResult(_hits);
	}

	private sealed class StubEmbeddings : IEmbeddingProvider
	{
		public string ProviderId => "stub";

		public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
			string model,
			IReadOnlyList<string> inputs,
			Guid correlationId,
			CancellationToken cancellationToken = default)
		{
			var v = new float[Dim];
			IReadOnlyList<ReadOnlyMemory<float>> vectors = new[] { new ReadOnlyMemory<float>(v) };
			return Task.FromResult(vectors);
		}
	}

	private sealed class StubReader : IChunkContentReader
	{
		private readonly IReadOnlyDictionary<Guid, string> _map;

		public StubReader(IReadOnlyDictionary<Guid, string> map) => _map = map;

		public Task<IReadOnlyDictionary<Guid, string>> ReadContentAsync(
			IReadOnlyCollection<Guid> chunkIds,
			int maxCharsPerChunk = 4096,
			CancellationToken cancellationToken = default)
		{
			var filtered = chunkIds
				.Where(_map.ContainsKey)
				.ToDictionary(id => id, id => _map[id]);
			return Task.FromResult<IReadOnlyDictionary<Guid, string>>(filtered);
		}
	}

	private sealed class ThrowingReader : IChunkContentReader
	{
		public Task<IReadOnlyDictionary<Guid, string>> ReadContentAsync(
			IReadOnlyCollection<Guid> chunkIds,
			int maxCharsPerChunk = 4096,
			CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("simulated postgres failure");
	}

	private sealed class RecordingReader : IChunkContentReader
	{
		public int CallCount { get; private set; }

		public Task<IReadOnlyDictionary<Guid, string>> ReadContentAsync(
			IReadOnlyCollection<Guid> chunkIds,
			int maxCharsPerChunk = 4096,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			return Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
		}
	}
}
