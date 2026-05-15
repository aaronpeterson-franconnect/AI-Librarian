using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Quality;
using AiLibrarian.Quality.Tests.Fixtures;

namespace AiLibrarian.Quality.Tests;

/// <summary>
/// Grader parser robustness. Real "is the model's verdict calibrated"
/// runs as part of the eval harness's nightly job (it needs the 20-case
/// calibration set + a live IChatProvider); these tests pin the parser
/// edges so a model emitting messy-but-valid JSON still produces a
/// usable verdict, and a malformed response degrades cleanly to
/// Unverifiable instead of throwing.
/// </summary>
public sealed class LlmClaimGraderTests
{
	[Fact]
	public async Task Clean_Json_Verdict_Parses()
	{
		var grader = MakeGrader("{\"verdict\":\"Supported\",\"confidence\":0.9,\"rationale\":\"matches source\"}");

		var claim = MakeClaim(out var chunkId);
		var chunks = new Dictionary<Guid, string> { [chunkId] = "the source text" };

		var grade = await grader.GradeAsync(claim, chunks);

		grade.Verdict.Should().Be(ClaimVerdict.Supported);
		grade.Confidence.Should().Be(0.9);
		grade.Rationale.Should().Be("matches source");
	}

	[Fact]
	public async Task Json_With_Trailing_Prose_Still_Parses()
	{
		var raw = "Here is my verdict: {\"verdict\":\"NotSupported\",\"confidence\":0.7,\"rationale\":\"no match\"} -- end.";
		var grader = MakeGrader(raw);
		var claim = MakeClaim(out var chunkId);
		var chunks = new Dictionary<Guid, string> { [chunkId] = "text" };

		var grade = await grader.GradeAsync(claim, chunks);

		grade.Verdict.Should().Be(ClaimVerdict.NotSupported);
	}

	[Fact]
	public async Task Nested_Object_Picks_Outer_Boundary()
	{
		// A real model sometimes emits {"verdict":"...","detail":{"x":1}}.
		// The parser must find the outermost matched braces.
		var raw = "{\"verdict\":\"Partial\",\"confidence\":0.6,\"rationale\":\"partial\",\"detail\":{\"x\":1}}";
		var grader = MakeGrader(raw);
		var claim = MakeClaim(out var chunkId);
		var chunks = new Dictionary<Guid, string> { [chunkId] = "text" };

		var grade = await grader.GradeAsync(claim, chunks);

		grade.Verdict.Should().Be(ClaimVerdict.Partial);
	}

	[Fact]
	public async Task Unknown_Verdict_Falls_Back_To_Unverifiable()
	{
		var grader = MakeGrader("{\"verdict\":\"Probably\",\"confidence\":0.8,\"rationale\":\"\"}");
		var claim = MakeClaim(out var chunkId);
		var chunks = new Dictionary<Guid, string> { [chunkId] = "text" };

		var grade = await grader.GradeAsync(claim, chunks);

		grade.Verdict.Should().Be(ClaimVerdict.Unverifiable);
		grade.Confidence.Should().Be(0.0);
	}

	[Fact]
	public async Task Confidence_Outside_Bounds_Is_Clamped()
	{
		var grader = MakeGrader("{\"verdict\":\"Supported\",\"confidence\":1.5,\"rationale\":\"x\"}");
		var claim = MakeClaim(out var chunkId);
		var chunks = new Dictionary<Guid, string> { [chunkId] = "text" };

		var grade = await grader.GradeAsync(claim, chunks);

		grade.Confidence.Should().Be(1.0);
	}

	[Fact]
	public async Task Empty_Response_Returns_Unverifiable()
	{
		var grader = MakeGrader(string.Empty);
		var claim = MakeClaim(out var chunkId);
		var chunks = new Dictionary<Guid, string> { [chunkId] = "text" };

		var grade = await grader.GradeAsync(claim, chunks);

		grade.Verdict.Should().Be(ClaimVerdict.Unverifiable);
	}

	[Fact]
	public async Task Missing_Chunk_Text_Still_Calls_Llm()
	{
		// The grader must not throw when the caller forgot to supply
		// chunk text -- it just renders "(no text available)" in the
		// prompt and lets the model decide.
		var grader = MakeGrader("{\"verdict\":\"Unverifiable\",\"confidence\":0.1,\"rationale\":\"no source\"}");
		var claim = MakeClaim(out _);

		var grade = await grader.GradeAsync(claim, new Dictionary<Guid, string>());

		grade.Verdict.Should().Be(ClaimVerdict.Unverifiable);
	}

	[Fact]
	public void Sink_Records_And_Returns_Latest_Grade()
	{
		var sink = new InMemoryClaimGradeSink();
		var claimId = Guid.NewGuid();
		sink.Record(new ClaimGrade(claimId, ClaimVerdict.Supported, 0.9, "first"));
		sink.Record(new ClaimGrade(claimId, ClaimVerdict.NotSupported, 0.6, "revised"));

		sink.TryGet(claimId, out var grade).Should().BeTrue();
		grade.Verdict.Should().Be(ClaimVerdict.NotSupported);
		sink.Snapshot().Should().ContainSingle();
	}

	private static LlmClaimGrader MakeGrader(string response) =>
		new(new FakeChatProvider().Returning(response));

	private static Claim MakeClaim(out Guid chunkId)
	{
		chunkId = Guid.NewGuid();
		return new Claim(
			Id: Guid.NewGuid(),
			Text: "the sky is blue",
			FacetClassification: Classification.Internal,
			Citations: new[] { new Citation(Guid.NewGuid(), chunkId, 0, 10, 0.9) });
	}
}
