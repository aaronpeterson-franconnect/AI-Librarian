using AiLibrarian.Domain.Ingest;
using AiLibrarian.IngestWorker;
using AiLibrarian.Skills.Markdown;

namespace AiLibrarian.IngestWorker.Tests;

public sealed class IngestSkillResolverTests
{
	private static SkillRegistry Registry() => new([new MarkdownSkill()]);

	[Fact]
	public void Resolve_prefers_mime_over_extension()
	{
		var job = new IngestJobMessage
		{
			BlobUri = "https://x.blob.core.windows.net/c/a.txt",
			ContentType = "text/markdown",
			OriginalFileName = "wrong.pdf",
		};
		IngestSkillResolver.Resolve(Registry(), job)!.Name.Should().Be("markdown");
	}

	[Fact]
	public void Resolve_falls_back_to_extension()
	{
		var job = new IngestJobMessage
		{
			BlobUri = "https://x.blob.core.windows.net/c/a.md",
			OriginalFileName = "notes.md",
		};
		IngestSkillResolver.Resolve(Registry(), job)!.Name.Should().Be("markdown");
	}

	[Fact]
	public void Resolve_returns_null_when_unmatched()
	{
		var job = new IngestJobMessage
		{
			BlobUri = "https://x.blob.core.windows.net/c/a.bin",
			ContentType = "application/octet-stream",
		};
		IngestSkillResolver.Resolve(Registry(), job).Should().BeNull();
	}
}
