using AiLibrarian.Api.Synthesis;

namespace AiLibrarian.Api.Tests.Synthesis;

/// <summary>
/// Pins the answer-to-claims parser. The shape these tests assert
/// drives the <c>/api/ask</c> response's claim breakdown that the eval
/// harness's citation-coverage metric consumes.
/// </summary>
public sealed class AskClaimBreakdownTests
{
	private static readonly Guid ChunkA = Guid.Parse("11111111-1111-1111-1111-aaaaaaaaaaaa");
	private static readonly Guid ChunkB = Guid.Parse("22222222-2222-2222-2222-bbbbbbbbbbbb");
	private static readonly Guid ChunkC = Guid.Parse("33333333-3333-3333-3333-cccccccccccc");

	[Fact]
	public void Null_or_empty_answer_yields_empty_breakdown()
	{
		AskClaimBreakdown.Extract(null).Claims.Should().BeEmpty();
		AskClaimBreakdown.Extract(string.Empty).Claims.Should().BeEmpty();
		AskClaimBreakdown.Extract("   \t\n").Claims.Should().BeEmpty();
	}

	[Fact]
	public void Each_sentence_becomes_one_claim()
	{
		var answer = "First fact. Second fact. Third fact.";
		var result = AskClaimBreakdown.Extract(answer);

		result.ClaimCount.Should().Be(3);
		result.Claims.Should().HaveCount(3);
		result.Claims[0].Text.Should().Be("First fact.");
		result.Claims[1].Text.Should().Be("Second fact.");
		result.Claims[2].Text.Should().Be("Third fact.");
	}

	[Fact]
	public void Citation_tokens_are_extracted_into_chunk_id_list()
	{
		var answer = $"Staging rotates every 90 days [chunk:{ChunkA:D}]. Production rotates weekly [chunk:{ChunkB:D}].";
		var result = AskClaimBreakdown.Extract(answer);

		result.ClaimCount.Should().Be(2);
		result.CitedClaimCount.Should().Be(2);
		result.Claims[0].ChunkIds.Should().BeEquivalentTo(new[] { ChunkA });
		result.Claims[1].ChunkIds.Should().BeEquivalentTo(new[] { ChunkB });
	}

	[Fact]
	public void Multiple_citations_on_one_sentence_accumulate()
	{
		var answer = $"Both runbooks agree [chunk:{ChunkA:D}] [chunk:{ChunkB:D}].";
		var result = AskClaimBreakdown.Extract(answer);

		result.Claims.Should().ContainSingle()
			.Which.ChunkIds.Should().BeEquivalentTo(new[] { ChunkA, ChunkB });
	}

	[Fact]
	public void Sentence_without_citation_counts_as_uncited()
	{
		var answer = $"Cited sentence [chunk:{ChunkA:D}]. Uncited sentence.";
		var result = AskClaimBreakdown.Extract(answer);

		result.ClaimCount.Should().Be(2);
		result.CitedClaimCount.Should().Be(1);
		result.Claims[0].ChunkIds.Should().BeEquivalentTo(new[] { ChunkA });
		result.Claims[1].ChunkIds.Should().BeEmpty();
	}

	[Fact]
	public void Rendered_text_strips_inline_citation_tokens()
	{
		var answer = $"The cap is 4096 chars [chunk:{ChunkA:D}].";
		var result = AskClaimBreakdown.Extract(answer);

		result.Claims[0].Text.Should().NotContain("[chunk:");
		result.Claims[0].Text.Should().Contain("cap is 4096");
	}

	[Fact]
	public void Answer_with_only_citation_tokens_produces_no_claims()
	{
		var answer = $"[chunk:{ChunkA:D}] [chunk:{ChunkB:D}]";
		var result = AskClaimBreakdown.Extract(answer);

		result.ClaimCount.Should().Be(0);
		result.Claims.Should().BeEmpty();
	}

	[Fact]
	public void Answer_without_any_citation_tokens_still_counts_sentences()
	{
		var answer = "First. Second. Third.";
		var result = AskClaimBreakdown.Extract(answer);

		result.ClaimCount.Should().Be(3);
		result.CitedClaimCount.Should().Be(0,
			"citation coverage drops to 0 when the prompt didn't ask for inline tokens; that's the signal to tighten the prompt");
	}

	[Fact]
	public void Malformed_chunk_token_is_ignored()
	{
		// The regex requires the 8-4-4-4-12 GUID shape; a non-GUID
		// inside the token doesn't match and the sentence stays
		// uncited.
		var answer = "Some claim [chunk:not-a-guid].";
		var result = AskClaimBreakdown.Extract(answer);

		result.ClaimCount.Should().Be(1);
		result.CitedClaimCount.Should().Be(0);
	}

	[Fact]
	public void Question_and_exclamation_terminators_split_too()
	{
		var answer = $"Does it rotate every 90 days? Yes [chunk:{ChunkA:D}]. Critical [chunk:{ChunkB:D}]!";
		var result = AskClaimBreakdown.Extract(answer);

		result.ClaimCount.Should().Be(3);
		result.CitedClaimCount.Should().Be(2);
	}

	[Fact]
	public void Three_citations_across_three_sentences_aggregates_distinct_chunks()
	{
		var answer = $"Alpha [chunk:{ChunkA:D}]. Beta [chunk:{ChunkB:D}]. Gamma [chunk:{ChunkC:D}].";
		var result = AskClaimBreakdown.Extract(answer);

		result.ClaimCount.Should().Be(3);
		result.CitedClaimCount.Should().Be(3);
		var allCited = result.Claims.SelectMany(c => c.ChunkIds).Distinct();
		allCited.Should().BeEquivalentTo(new[] { ChunkA, ChunkB, ChunkC });
	}
}
