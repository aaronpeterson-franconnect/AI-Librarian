using System.Runtime.CompilerServices;

using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.WikiMaintainer;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.WikiMaintainer.Tests;

/// <summary>
/// Pins the cosine math + batching contract + fallback shape for the
/// embedding-similarity scorer. No real embedding provider — a
/// programmable stub returns canned vectors so similarity values are
/// deterministic.
/// </summary>
public sealed class EmbeddingSimilarityConfidenceScorerTests
{
	private static readonly Guid ChunkA = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid ChunkB = Guid.Parse("22222222-2222-2222-2222-222222222222");

	[Fact]
	public async Task Empty_Claims_Returns_Input_Unchanged()
	{
		var scorer = MakeScorer(new ProgrammableEmbedder());

		var result = await scorer.ScoreAsync(
			Array.Empty<WikiClaimDraft>(),
			Array.Empty<WikiMaintenanceSourceChunk>());

		result.Should().BeEmpty();
	}

	[Fact]
	public async Task Missing_Deployment_Returns_Input_Unchanged()
	{
		// EmbeddingDeployment="" -> scorer logs + returns claims as-is.
		var scorer = new EmbeddingSimilarityConfidenceScorer(
			new ProgrammableEmbedder(),
			Options.Create(new EmbeddingSimilarityScorerOptions { EmbeddingDeployment = string.Empty }),
			NullLogger<EmbeddingSimilarityConfidenceScorer>.Instance);

		var claims = new[] { MakeClaim(citationChunkIds: new[] { ChunkA }, placeholderConfidence: 0.85) };
		var pool = new[] { Chunk(ChunkA, "alpha") };

		var result = await scorer.ScoreAsync(claims, pool);

		result.Should().HaveCount(1);
		result[0].Citations[0].Confidence.Should().Be(0.85);
	}

	[Fact]
	public async Task Cosine_Similarity_Replaces_Placeholder()
	{
		// Two parallel unit vectors -> similarity = 1.0.
		var embedder = new ProgrammableEmbedder
		{
			ResponseByText =
			{
				["the worker boots cleanly"] = new[] { 1.0f, 0.0f, 0.0f },
				["alpha"] = new[] { 1.0f, 0.0f, 0.0f },
			},
		};

		var scorer = MakeScorer(embedder);
		var claims = new[] { MakeClaim("the worker boots cleanly", new[] { ChunkA }, 0.5) };
		var pool = new[] { Chunk(ChunkA, "alpha") };

		var result = await scorer.ScoreAsync(claims, pool);

		result[0].Citations[0].Confidence.Should().BeApproximately(1.0, 0.001);
	}

	[Fact]
	public async Task Orthogonal_Vectors_Score_Zero()
	{
		var embedder = new ProgrammableEmbedder
		{
			ResponseByText =
			{
				["claim"] = new[] { 1.0f, 0.0f, 0.0f },
				["chunk"] = new[] { 0.0f, 1.0f, 0.0f },
			},
		};

		var scorer = MakeScorer(embedder);
		var claims = new[] { MakeClaim("claim", new[] { ChunkA }, 0.5) };
		var pool = new[] { Chunk(ChunkA, "chunk") };

		var result = await scorer.ScoreAsync(claims, pool);

		result[0].Citations[0].Confidence.Should().BeApproximately(0.0, 0.001);
	}

	[Fact]
	public async Task Negative_Cosine_Clamps_To_Zero()
	{
		// Cosine of opposite-direction vectors is -1; clamped to 0.
		var embedder = new ProgrammableEmbedder
		{
			ResponseByText =
			{
				["claim"] = new[] { 1.0f, 0.0f, 0.0f },
				["chunk"] = new[] { -1.0f, 0.0f, 0.0f },
			},
		};

		var scorer = MakeScorer(embedder);
		var claims = new[] { MakeClaim("claim", new[] { ChunkA }, 0.5) };
		var pool = new[] { Chunk(ChunkA, "chunk") };

		var result = await scorer.ScoreAsync(claims, pool);

		result[0].Citations[0].Confidence.Should().Be(0.0);
	}

	[Fact]
	public async Task Batches_Distinct_Texts_Per_Call()
	{
		// Two claims sharing text + two chunks with distinct text =>
		// 1 distinct claim text + 2 distinct chunk texts => 2 batched
		// EmbedAsync calls total, one with 1 input and one with 2.
		var embedder = new ProgrammableEmbedder
		{
			ResponseByText =
			{
				["shared claim"] = new[] { 1.0f, 0.0f },
				["alpha"] = new[] { 1.0f, 0.0f },
				["beta"] = new[] { 0.5f, 0.866f },
			},
		};

		var scorer = MakeScorer(embedder);
		var claims = new[]
		{
			MakeClaim("shared claim", new[] { ChunkA }, 0.5),
			MakeClaim("shared claim", new[] { ChunkB }, 0.5),
		};
		var pool = new[] { Chunk(ChunkA, "alpha"), Chunk(ChunkB, "beta") };

		var result = await scorer.ScoreAsync(claims, pool);

		embedder.Calls.Should().HaveCount(2, "one batched call for claim texts + one for chunk texts");
		embedder.Calls[0].Inputs.Should().HaveCount(1, "claim text deduplicated");
		embedder.Calls[1].Inputs.Should().HaveCount(2, "chunk pool has two distinct texts");

		result[0].Citations[0].Confidence.Should().BeApproximately(1.0, 0.001);
		result[1].Citations[0].Confidence.Should().BeApproximately(0.5, 0.001);
	}

	[Fact]
	public async Task Embedding_Failure_Falls_Back_To_Placeholder()
	{
		var embedder = new ProgrammableEmbedder { ThrowOnEmbed = true };
		var scorer = MakeScorer(embedder);

		var claims = new[] { MakeClaim("c", new[] { ChunkA }, 0.85) };
		var pool = new[] { Chunk(ChunkA, "alpha") };

		var result = await scorer.ScoreAsync(claims, pool);

		// The scorer logs and returns the input claims unchanged.
		result.Should().HaveCount(1);
		result[0].Citations[0].Confidence.Should().Be(0.85);
	}

	[Fact]
	public async Task Citation_To_Unknown_Chunk_Preserves_Input_Confidence()
	{
		var unknown = Guid.NewGuid();
		var embedder = new ProgrammableEmbedder
		{
			ResponseByText =
			{
				["claim"] = new[] { 1.0f, 0.0f },
				["alpha"] = new[] { 1.0f, 0.0f },
			},
		};

		var scorer = MakeScorer(embedder);
		// Two citations on the claim: one to a known chunk, one to a
		// chunk that's not in the pool. The known one gets scored;
		// the unknown one keeps the input value.
		var claim = new WikiClaimDraft(
			ClaimText: "claim",
			Position: 0,
			Citations: new[]
			{
				new Citation(Guid.NewGuid(), ChunkA, 0, 100, 0.5),
				new Citation(Guid.NewGuid(), unknown, 0, 100, 0.5),
			});
		var pool = new[] { Chunk(ChunkA, "alpha") };

		var result = await scorer.ScoreAsync(new[] { claim }, pool);

		result[0].Citations[0].Confidence.Should().BeApproximately(1.0, 0.001);
		result[0].Citations[1].Confidence.Should().Be(0.5);
	}

	[Fact]
	public async Task PlaceholderScorer_Is_A_Pure_NoOp()
	{
		var scorer = new PlaceholderConfidenceScorer();
		var claims = new[] { MakeClaim("c", new[] { ChunkA }, 0.85) };
		var pool = new[] { Chunk(ChunkA, "alpha") };

		var result = await scorer.ScoreAsync(claims, pool);

		// Same reference -- the placeholder doesn't allocate.
		result.Should().BeSameAs(claims);
	}

	private static EmbeddingSimilarityConfidenceScorer MakeScorer(ProgrammableEmbedder embedder)
		=> new(
			embedder,
			Options.Create(new EmbeddingSimilarityScorerOptions { EmbeddingDeployment = "test-deploy" }),
			NullLogger<EmbeddingSimilarityConfidenceScorer>.Instance);

	private static WikiClaimDraft MakeClaim(string text, IReadOnlyList<Guid> citationChunkIds, double placeholderConfidence)
	{
		var citations = citationChunkIds
			.Select(id => new Citation(Guid.NewGuid(), id, 0, 100, placeholderConfidence))
			.ToArray();
		return new WikiClaimDraft(ClaimText: text, Position: 0, Citations: citations);
	}

	private static WikiClaimDraft MakeClaim(IReadOnlyList<Guid> citationChunkIds, double placeholderConfidence)
		=> MakeClaim("test claim text", citationChunkIds, placeholderConfidence);

	private static WikiMaintenanceSourceChunk Chunk(Guid id, string text)
		=> new(ChunkId: id, ContentMarkdown: text, Classification: Classification.Internal);

	private sealed class ProgrammableEmbedder : IEmbeddingProvider
	{
		private static readonly float[] ZeroVector = new[] { 0.0f, 0.0f, 0.0f };

		public string ProviderId => "test";
		public bool ThrowOnEmbed { get; set; }
		public Dictionary<string, float[]> ResponseByText { get; } = new(StringComparer.Ordinal);
		public List<EmbedCall> Calls { get; } = new();

		public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
			string deployment,
			IReadOnlyList<string> inputs,
			Guid correlationId,
			CancellationToken cancellationToken = default)
		{
			Calls.Add(new EmbedCall(deployment, inputs.ToArray()));
			if (ThrowOnEmbed)
			{
				throw new InvalidOperationException("simulated embedding outage");
			}

			var vectors = inputs
				.Select(input => new ReadOnlyMemory<float>(ResponseByText.TryGetValue(input, out var v) ? v : ZeroVector))
				.ToList();
			return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(vectors);
		}

		public sealed record EmbedCall(string Deployment, IReadOnlyList<string> Inputs);
	}
}
