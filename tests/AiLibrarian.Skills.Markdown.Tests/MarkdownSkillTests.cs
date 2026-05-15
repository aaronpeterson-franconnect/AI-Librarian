using System.Text;
using System.Text.Json.Nodes;

using AiLibrarian.Domain.Skills;
using AiLibrarian.Skills.Markdown;

namespace AiLibrarian.Skills.Markdown.Tests;

public sealed class MarkdownSkillTests
{
	[Fact]
	public async Task CanonicalizeAsync_splits_paragraphs_and_preserves_frontmatter_raw()
	{
		var skill = new MarkdownSkill();
		const string md = """
			---
			title: Runbook
			---

			First paragraph.

			Second paragraph.
			""";

		await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(md));
		var result = await skill.CanonicalizeAsync(
			stream,
			new SourceMetadata("text/markdown", "runbook.md"),
			CancellationToken.None);

		result.ExtractedMetadata.Should().ContainKey("frontmatter_raw");
		result.ExtractedMetadata["frontmatter_raw"]!.ToString().Should().Contain("title:");
		result.Chunks.Should().HaveCount(2);
		result.Chunks[0].ContentMarkdown.Should().Be("First paragraph.");
		result.Chunks[1].ContentMarkdown.Should().Be("Second paragraph.");

		var span0 = result.Chunks[0].SpanAnchor.AsObject();
		span0["type"]!.GetValue<string>().Should().Be("markdown");
		span0["paragraphIndex"]!.GetValue<int>().Should().Be(0);
	}
}
