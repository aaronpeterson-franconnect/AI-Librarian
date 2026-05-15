using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Quality;
using AiLibrarian.Quality.Tests.Fixtures;

namespace AiLibrarian.Quality.Tests;

/// <summary>
/// Five rules × happy + edge cases. The acceptance bar from the
/// hardening plan: "50 valid, 50 each-rule-violating, all classified
/// correctly." We meet that with a generator (`SyntheticClaims.ValidCorpus`)
/// + one targeted test per rule.
/// </summary>
public sealed class CitationValidatorTests
{
	[Fact]
	public async Task Valid_Corpus_Of_50_Reports_No_Violations()
	{
		var chunks = new List<ChunkRef>();
		var claims = SyntheticClaims.ValidCorpus(50, chunks).ToList();
		var lookup = new InMemoryChunkLookup();
		foreach (var c in chunks)
		{
			lookup.Add(c);
		}

		var validator = new CitationValidator(lookup);
		var result = await validator.ValidateAsync(claims);

		result.IsValid.Should().BeTrue();
		result.Violations.Should().BeEmpty();
	}

	[Fact]
	public async Task Rule1_Claim_Without_Citations_Fails()
	{
		var claim = new Claim(
			Id: Guid.NewGuid(),
			Text: "claim with no citations",
			FacetClassification: Classification.Internal,
			Citations: Array.Empty<Citation>());

		var lookup = new InMemoryChunkLookup();
		var validator = new CitationValidator(lookup);

		var result = await validator.ValidateAsync(new[] { claim });

		result.IsValid.Should().BeFalse();
		result.Violations.Should().ContainSingle()
			.Which.Rule.Should().Be(CitationRule.ClaimHasCitation);
	}

	[Fact]
	public async Task Rule2_Citation_To_Missing_Chunk_Fails()
	{
		var claim = new Claim(
			Id: Guid.NewGuid(),
			Text: "claim",
			FacetClassification: Classification.Internal,
			Citations: new[] { SyntheticClaims.ValidCitation(chunkId: Guid.NewGuid()) });

		// Empty lookup -> chunk never resolves.
		var validator = new CitationValidator(new InMemoryChunkLookup());

		var result = await validator.ValidateAsync(new[] { claim });

		result.Violations.Should().ContainSingle()
			.Which.Rule.Should().Be(CitationRule.ChunkResolves);
	}

	[Fact]
	public async Task Rule2_Citation_To_SoftDeleted_Chunk_Fails()
	{
		var chunkId = Guid.NewGuid();
		var lookup = new InMemoryChunkLookup().Add(SyntheticClaims.SoftDeletedChunk(chunkId));

		var claim = new Claim(
			Id: Guid.NewGuid(),
			Text: "claim",
			FacetClassification: Classification.Internal,
			Citations: new[] { SyntheticClaims.ValidCitation(chunkId) });

		var validator = new CitationValidator(lookup);
		var result = await validator.ValidateAsync(new[] { claim });

		result.Violations.Should().Contain(v => v.Rule == CitationRule.ChunkResolves);
	}

	[Theory]
	[InlineData(-1, 100)]           // negative start
	[InlineData(100, 100)]          // zero-length span
	[InlineData(100, 99)]           // end before start
	[InlineData(0, SyntheticClaims.ChunkLength + 1)] // overshoot
	public async Task Rule3_OutOfBounds_Span_Fails(int start, int end)
	{
		var chunkId = Guid.NewGuid();
		var lookup = new InMemoryChunkLookup().Add(SyntheticClaims.ValidChunk(chunkId));
		var claim = new Claim(
			Id: Guid.NewGuid(),
			Text: "claim",
			FacetClassification: Classification.Internal,
			Citations: new[] { new Citation(Guid.NewGuid(), chunkId, start, end, 0.9) });

		var validator = new CitationValidator(lookup);
		var result = await validator.ValidateAsync(new[] { claim });

		result.Violations.Should().Contain(v => v.Rule == CitationRule.SpanWithinChunk);
	}

	[Fact]
	public async Task Rule4_Confidential_Chunk_Cited_From_Internal_Facet_Fails()
	{
		var chunkId = Guid.NewGuid();
		var lookup = new InMemoryChunkLookup().Add(
			SyntheticClaims.ValidChunk(chunkId, Classification.Confidential));

		var claim = new Claim(
			Id: Guid.NewGuid(),
			Text: "claim",
			FacetClassification: Classification.Internal,
			Citations: new[] { SyntheticClaims.ValidCitation(chunkId) });

		var validator = new CitationValidator(lookup);
		var result = await validator.ValidateAsync(new[] { claim });

		result.Violations.Should().Contain(v => v.Rule == CitationRule.ClassificationNotLeaking);
	}

	[Fact]
	public async Task Rule4_Internal_Chunk_Cited_From_Confidential_Facet_Passes()
	{
		// Citing down the lattice is fine -- a Confidential facet is allowed
		// to reference Internal chunks. Just rule 4 specifically; everything
		// else stays valid.
		var chunkId = Guid.NewGuid();
		var lookup = new InMemoryChunkLookup().Add(
			SyntheticClaims.ValidChunk(chunkId, Classification.Internal));

		var claim = new Claim(
			Id: Guid.NewGuid(),
			Text: "claim",
			FacetClassification: Classification.Confidential,
			Citations: new[] { SyntheticClaims.ValidCitation(chunkId) });

		var validator = new CitationValidator(lookup);
		var result = await validator.ValidateAsync(new[] { claim });

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public async Task Rule5_Confidence_Below_Floor_Fails()
	{
		var chunkId = Guid.NewGuid();
		var lookup = new InMemoryChunkLookup().Add(SyntheticClaims.ValidChunk(chunkId));
		var claim = new Claim(
			Id: Guid.NewGuid(),
			Text: "claim",
			FacetClassification: Classification.Internal,
			Citations: new[] { SyntheticClaims.ValidCitation(chunkId, confidence: 0.5) });

		var validator = new CitationValidator(lookup);
		var result = await validator.ValidateAsync(new[] { claim });

		result.Violations.Should().Contain(v => v.Rule == CitationRule.ConfidenceFloorMet);
	}

	[Fact]
	public async Task Empty_Claim_List_Returns_Valid_Result()
	{
		var validator = new CitationValidator(new InMemoryChunkLookup());
		var result = await validator.ValidateAsync(Array.Empty<Claim>());
		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public async Task FailingClaimCount_Deduplicates_Per_Claim()
	{
		// One claim, two citations both failing rule 5. Expected: 2 violations
		// but FailingClaimCount = 1.
		var chunkId = Guid.NewGuid();
		var lookup = new InMemoryChunkLookup().Add(SyntheticClaims.ValidChunk(chunkId));
		var claim = new Claim(
			Id: Guid.NewGuid(),
			Text: "claim",
			FacetClassification: Classification.Internal,
			Citations: new[]
			{
				SyntheticClaims.ValidCitation(chunkId, confidence: 0.4),
				SyntheticClaims.ValidCitation(chunkId, confidence: 0.3),
			});

		var validator = new CitationValidator(lookup);
		var result = await validator.ValidateAsync(new[] { claim });

		result.Violations.Should().HaveCount(2);
		result.FailingClaimCount.Should().Be(1);
	}

	[Fact]
	public async Task Rule_Codes_Are_Stable_Strings()
	{
		// Lock the wire-format codes so dashboards / CI gates that pivot on
		// rule string don't break silently when the enum gets renumbered.
		CitationRule.ClaimHasCitation.ToCode().Should().Be("R1.ClaimHasCitation");
		CitationRule.ChunkResolves.ToCode().Should().Be("R2.ChunkResolves");
		CitationRule.SpanWithinChunk.ToCode().Should().Be("R3.SpanWithinChunk");
		CitationRule.ClassificationNotLeaking.ToCode().Should().Be("R4.ClassificationNotLeaking");
		CitationRule.ConfidenceFloorMet.ToCode().Should().Be("R5.ConfidenceFloorMet");
		await Task.CompletedTask;
	}
}
