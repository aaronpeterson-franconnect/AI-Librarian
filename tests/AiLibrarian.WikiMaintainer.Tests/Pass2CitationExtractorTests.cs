using AiLibrarian.Domain;
using AiLibrarian.WikiMaintainer;

namespace AiLibrarian.WikiMaintainer.Tests;

/// <summary>
/// Pure, deterministic parser tests. No LLM, no DB. These pin the
/// shape Pass 2 produces given known Pass 1 outputs — both happy
/// paths and the malformed-LLM-output cases the validator will end
/// up catching downstream.
/// </summary>
public sealed class Pass2CitationExtractorTests
{
	private static readonly Guid ChunkA = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid ChunkB = Guid.Parse("22222222-2222-2222-2222-222222222222");
	private static readonly Guid UnknownChunk = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

	[Fact]
	public void Single_Sentence_With_One_Citation_Produces_One_Claim()
	{
		var (extractor, pool) = Setup();
		var prose = "The worker boots cleanly on cold start. [chunk:11111111-1111-1111-1111-111111111111]";

		var result = extractor.Extract(prose, pool, out var warnings);

		warnings.Should().BeEmpty();
		result.Claims.Should().ContainSingle();
		result.Claims[0].ClaimText.Should().Be("The worker boots cleanly on cold start.");
		result.Claims[0].Citations.Should().ContainSingle();
		result.Claims[0].Citations[0].ChunkId.Should().Be(ChunkA);
		// Citation token stripped from the rendered body.
		result.BodyMarkdown.Should().NotContain("[chunk:");
	}

	[Fact]
	public void Multi_Sentence_Splits_On_Period_Whitespace()
	{
		var (extractor, pool) = Setup();
		var prose = "First fact. [chunk:11111111-1111-1111-1111-111111111111] Second fact. [chunk:22222222-2222-2222-2222-222222222222]";

		var result = extractor.Extract(prose, pool, out _);

		result.Claims.Should().HaveCount(2);
		result.Claims[0].ClaimText.Should().Contain("First fact");
		result.Claims[1].ClaimText.Should().Contain("Second fact");
		result.Claims[0].Citations.Should().ContainSingle().Which.ChunkId.Should().Be(ChunkA);
		result.Claims[1].Citations.Should().ContainSingle().Which.ChunkId.Should().Be(ChunkB);
	}

	[Fact]
	public void Multi_Citation_In_Single_Sentence_Attaches_Both()
	{
		var (extractor, pool) = Setup();
		var prose = "Both sources agree. [chunk:11111111-1111-1111-1111-111111111111] [chunk:22222222-2222-2222-2222-222222222222]";

		var result = extractor.Extract(prose, pool, out _);

		result.Claims.Should().ContainSingle();
		result.Claims[0].Citations.Should().HaveCount(2);
		result.Claims[0].Citations.Select(c => c.ChunkId).Should().BeEquivalentTo(new[] { ChunkA, ChunkB });
	}

	[Fact]
	public void Duplicate_Citation_In_One_Claim_Is_Deduplicated()
	{
		var (extractor, pool) = Setup();
		// The same chunk cited twice in one sentence. The
		// wiki_claim_citations UNIQUE (claim_id, chunk_id) would reject
		// the second; Pass 2 collapses them upfront.
		var prose = "Single fact. [chunk:11111111-1111-1111-1111-111111111111] [chunk:11111111-1111-1111-1111-111111111111]";

		var result = extractor.Extract(prose, pool, out _);

		result.Claims.Should().ContainSingle();
		result.Claims[0].Citations.Should().ContainSingle();
	}

	[Fact]
	public void Citation_To_Unknown_Chunk_Is_Dropped_With_Warning()
	{
		var (extractor, pool) = Setup();
		// The unknown-chunk citation gets dropped; the claim is
		// retained but has NO citations -- rule 1 will reject it
		// downstream. That's the desired contract: the validator,
		// not the parser, owns the rejection.
		var prose = $"Suspicious claim. [chunk:{UnknownChunk:D}]";

		var result = extractor.Extract(prose, pool, out var warnings);

		result.Claims.Should().ContainSingle();
		result.Claims[0].Citations.Should().BeEmpty();
		warnings.Should().Contain(w => w.Contains("NOT in the source pool"));
		warnings.Should().Contain(w => w.Contains("has no [chunk:...] citation token"));
	}

	[Fact]
	public void Claim_Without_Any_Citation_Emits_Warning_But_Retains_The_Claim()
	{
		var (extractor, pool) = Setup();
		var prose = "I forgot to cite. Next sentence. [chunk:11111111-1111-1111-1111-111111111111]";

		var result = extractor.Extract(prose, pool, out var warnings);

		// Two claims emitted; the first has no citations and a warning.
		result.Claims.Should().HaveCount(2);
		result.Claims[0].Citations.Should().BeEmpty();
		warnings.Should().Contain(w => w.Contains("has no [chunk:...]"));
	}

	[Fact]
	public void Markdown_Headings_Are_Split_As_Separate_Claims()
	{
		var (extractor, pool) = Setup();
		var prose = "# Overview\nFirst body sentence. [chunk:11111111-1111-1111-1111-111111111111]";

		var result = extractor.Extract(prose, pool, out _);

		result.Claims.Should().HaveCount(2);
		result.Claims[0].ClaimText.Should().StartWith("# ");
		result.Claims[1].ClaimText.Should().Contain("First body");
	}

	[Fact]
	public void Empty_Or_Whitespace_Prose_Produces_Zero_Claims()
	{
		var (extractor, pool) = Setup();

		var result = extractor.Extract("   \n\n   ", pool, out _);
		result.Claims.Should().BeEmpty();
		result.BodyMarkdown.Should().BeEmpty();
	}

	[Fact]
	public void Confidence_Defaults_Match_Options()
	{
		var (extractor, pool) = Setup(new WikiMaintainerOptions { DefaultCitationConfidence = 0.93 });
		var prose = "Claim. [chunk:11111111-1111-1111-1111-111111111111]";

		var result = extractor.Extract(prose, pool, out _);

		result.Claims[0].Citations[0].Confidence.Should().Be(0.93);
	}

	[Fact]
	public void Span_End_Clamps_To_Chunk_Length()
	{
		// Tiny chunk content + large default span -- the extractor must
		// clamp so the schema's span-bounds CHECK constraint isn't
		// violated.
		var pool = new List<WikiMaintenanceSourceChunk>
		{
			new(ChunkA, "x", Classification.Internal),
		};
		var extractor = new Pass2CitationExtractor(new WikiMaintainerOptions { DefaultCitationSpanLength = 1000 });

		var prose = "Tiny chunk claim. [chunk:11111111-1111-1111-1111-111111111111]";
		var result = extractor.Extract(prose, pool, out _);

		var citation = result.Claims[0].Citations[0];
		citation.SpanStart.Should().Be(0);
		citation.SpanEnd.Should().BeLessThanOrEqualTo(1);
		citation.SpanEnd.Should().BeGreaterThan(citation.SpanStart);
	}

	private static (Pass2CitationExtractor Extractor, List<WikiMaintenanceSourceChunk> Pool) Setup(WikiMaintainerOptions? options = null)
	{
		var pool = new List<WikiMaintenanceSourceChunk>
		{
			new(ChunkA, new string('a', 600), Classification.Internal),
			new(ChunkB, new string('b', 600), Classification.Internal),
		};

		var extractor = new Pass2CitationExtractor(options ?? new WikiMaintainerOptions());
		return (extractor, pool);
	}
}
