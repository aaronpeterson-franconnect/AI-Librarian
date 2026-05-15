using AiLibrarian.Domain;

namespace AiLibrarian.Eval.Loader;

public sealed class GoldenCaseLoaderTests
{
	private static string GoldenSetsDir
		=> Path.Combine(AppContext.BaseDirectory, "golden-sets", "engineering");

	[Fact]
	public void LoadAll_returns_cases_in_lexical_order()
	{
		var cases = GoldenCaseLoader.LoadAll(GoldenSetsDir);

		cases.Should().HaveCountGreaterThanOrEqualTo(2,
			because: "the example fixtures shipped with the project must be discovered.");

		var ids = cases.Select(c => c.Id).ToList();
		ids.Should().ContainInOrder(
			"secrets-rotation-runbook",
			"must-refuse-credentials-leak");
	}

	[Fact]
	public void LoadAll_parses_must_refuse_flag()
	{
		var cases = GoldenCaseLoader.LoadAll(GoldenSetsDir);

		var refusal = cases.Single(c => c.Id == "must-refuse-credentials-leak");
		refusal.MustRefuse.Should().BeTrue();
		refusal.ExpectedChunkIds.Should().BeEmpty();
		refusal.ClassificationScope.Should().Be(Classification.Internal);
	}

	[Fact]
	public void LoadAll_parses_expected_chunks_and_citations()
	{
		var cases = GoldenCaseLoader.LoadAll(GoldenSetsDir);

		var runbook = cases.Single(c => c.Id == "secrets-rotation-runbook");
		runbook.ExpectedChunkIds.Should().HaveCount(2);
		runbook.ExpectedCitations.Should().ContainSingle()
			.Which.MinConfidence.Should().Be(0.7);
		runbook.Tags.Should().ContainKey("category").WhoseValue.Should().Be("runbook");
	}

	[Fact]
	public void LoadAll_returns_empty_when_directory_missing()
	{
		GoldenCaseLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "no-such-dir"))
			.Should().BeEmpty();
	}
}
