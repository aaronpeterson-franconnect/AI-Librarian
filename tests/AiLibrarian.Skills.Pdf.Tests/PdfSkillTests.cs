using AiLibrarian.Domain.Skills;
using AiLibrarian.Skills.Pdf;

namespace AiLibrarian.Skills.Pdf.Tests;

public sealed class PdfSkillTests
{
	private static readonly SourceMetadata Metadata = new("application/pdf", "doc.pdf");
	private static readonly string[] ThreePages = ["Page one body.", "Page two body.", "Page three body."];
	private static readonly string[] FourPages = ["a", "b", "c", "d"];

	[Fact]
	public async Task Single_Page_PDF_Produces_One_Chunk()
	{
		var skill = new PdfSkill();
		await using var stream = PdfFixtureBuilder.BuildSinglePage("Hello librarian.");

		var result = await skill.CanonicalizeAsync(stream, Metadata, CancellationToken.None);

		result.Issues.Should().BeEmpty();
		result.Chunks.Should().HaveCount(1);
		result.Chunks[0].ContentMarkdown.Should().StartWith("## Page 1");
		result.Chunks[0].ContentMarkdown.Should().Contain("Hello librarian.");

		var span = result.Chunks[0].SpanAnchor.AsObject();
		span["type"]!.GetValue<string>().Should().Be("pdf");
		span["pageNumber"]!.GetValue<int>().Should().Be(1);
	}

	[Fact]
	public async Task Multi_Page_PDF_Emits_One_Chunk_Per_Page_In_Order()
	{
		var skill = new PdfSkill();
		await using var stream = PdfFixtureBuilder.BuildMultiPage(ThreePages);

		var result = await skill.CanonicalizeAsync(stream, Metadata, CancellationToken.None);

		result.Issues.Should().BeEmpty();
		result.Chunks.Should().HaveCount(3);
		result.Chunks[0].ContentMarkdown.Should().Contain("Page one body.");
		result.Chunks[1].ContentMarkdown.Should().Contain("Page two body.");
		result.Chunks[2].ContentMarkdown.Should().Contain("Page three body.");

		// Order index advances per chunk
		result.Chunks[0].OrderIndex.Should().Be(0);
		result.Chunks[2].OrderIndex.Should().Be(2);
	}

	[Fact]
	public async Task Empty_Page_PDF_Emits_NoText_Warning()
	{
		var skill = new PdfSkill();
		await using var stream = PdfFixtureBuilder.BuildEmptyPage();

		var result = await skill.CanonicalizeAsync(stream, Metadata, CancellationToken.None);

		result.Chunks.Should().BeEmpty();
		result.Issues.Should().Contain(i => i.Code == "pdf.no_text");
	}

	[Fact]
	public async Task Metadata_Reports_Page_Count()
	{
		var skill = new PdfSkill();
		await using var stream = PdfFixtureBuilder.BuildMultiPage(FourPages);

		var result = await skill.CanonicalizeAsync(stream, Metadata, CancellationToken.None);

		result.ExtractedMetadata.Should().ContainKey("page_count");
		result.ExtractedMetadata["page_count"]!.GetValue<int>().Should().Be(4);
	}

	[Fact]
	public async Task Malformed_PDF_Returns_ParseFailed_Issue_Not_Throw()
	{
		var skill = new PdfSkill();
		await using var stream = new MemoryStream(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }); // "Hello"

		var result = await skill.CanonicalizeAsync(stream, Metadata, CancellationToken.None);

		result.Chunks.Should().BeEmpty();
		result.Issues.Should().Contain(i => i.Code == "pdf.parse_failed" || i.Code == "pdf.encrypted");
	}

	[Fact]
	public async Task Supports_Standard_And_Alternate_Mime_Types()
	{
		var skill = new PdfSkill();
		skill.SupportedMimeTypes.Should().Contain("application/pdf");
		skill.SupportedMimeTypes.Should().Contain("application/x-pdf");
		skill.SupportedExtensions.Should().Contain(".pdf");
		skill.Name.Should().Be("pdf");
		await Task.CompletedTask;
	}

	[Theory]
	[InlineData("  foo   bar  ", "foo bar")]
	[InlineData("line one\nline two", "line one line two")]    // join hard wraps
	[InlineData("para one\n\npara two", "para one\n\npara two")] // preserve real breaks
	[InlineData("docu-\nmentation", "documentation")]            // hyphenated wrap join
	[InlineData("", "")]
	public void Normalize_Whitespace_And_Linebreaks(string input, string expected)
	{
		PdfSkill.Normalize(input).Should().Be(expected);
	}
}
