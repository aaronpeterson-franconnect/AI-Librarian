using AiLibrarian.IngestWorker;
using AiLibrarian.Skills.Markdown;

namespace AiLibrarian.IngestWorker.Tests;

public sealed class SkillRegistryTests
{
	[Fact]
	public void ResolveByMimeType_finds_markdown_skill()
	{
		var registry = new SkillRegistry([new MarkdownSkill()]);
		var skill = registry.ResolveByMimeType("text/markdown");
		skill.Should().NotBeNull();
		skill!.Name.Should().Be("markdown");
	}

	[Fact]
	public void ResolveByExtension_normalizes_leading_dot()
	{
		var registry = new SkillRegistry([new MarkdownSkill()]);
		registry.ResolveByExtension("md")!.Name.Should().Be("markdown");
		registry.ResolveByExtension(".md")!.Name.Should().Be("markdown");
	}
}
