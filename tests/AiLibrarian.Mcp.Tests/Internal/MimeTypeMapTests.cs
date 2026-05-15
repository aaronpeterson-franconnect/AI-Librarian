using AiLibrarian.Mcp.Internal;

namespace AiLibrarian.Mcp.Tests.Internal;

/// <summary>
/// Pin the extension → MIME mapping used by <c>submit_source</c>.
/// Phase 1 ingest worker registers Skill plugins keyed on these MIME
/// types, so a silent change to this table changes which Skill picks
/// up which file. Tests guard against accidental drift.
/// </summary>
public sealed class MimeTypeMapTests
{
	[Theory]
	[InlineData("/tmp/runbook.md", "text/markdown")]
	[InlineData("C:\\runbooks\\Note.MARKDOWN", "text/markdown")]
	[InlineData("design.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
	[InlineData("budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
	[InlineData("Q3-deck.PPTX", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
	[InlineData("spec.pdf", "application/pdf")]
	[InlineData("readme.txt", "text/plain")]
	[InlineData("data.csv", "text/csv")]
	[InlineData("payload.json", "application/json")]
	[InlineData("page.html", "text/html")]
	public void ResolveByPath_returns_known_mime_for_supported_extensions(string path, string expected)
	{
		MimeTypeMap.ResolveByPath(path).Should().Be(expected);
	}

	[Theory]
	[InlineData("archive.zip")]
	[InlineData("photo.heic")]
	[InlineData("trace.bin")]
	[InlineData("nofile_no_extension")]
	public void ResolveByPath_returns_octet_stream_for_unknown_or_missing_extensions(string path)
	{
		MimeTypeMap.ResolveByPath(path).Should().Be("application/octet-stream");
	}

	[Fact]
	public void ResolveByPath_throws_on_empty_path()
	{
		Action act = () => MimeTypeMap.ResolveByPath(string.Empty);
		act.Should().Throw<ArgumentException>();
	}
}
