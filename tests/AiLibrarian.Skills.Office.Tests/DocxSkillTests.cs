using AiLibrarian.Domain.Skills;
using AiLibrarian.Skills.Office;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AiLibrarian.Skills.Office.Tests;

/// <summary>
/// Builds tiny DOCX fixtures programmatically so test data is
/// inspectable in source rather than checked-in binary.
/// </summary>
public sealed class DocxSkillTests
{
	[Fact]
	public async Task CanonicalizeAsync_emits_paragraph_chunks_with_heading_markdown()
	{
		var skill = new DocxSkill();
		await using var stream = BuildHelloDocx();

		var result = await skill.CanonicalizeAsync(
			stream,
			new SourceMetadata("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "doc.docx"),
			CancellationToken.None);

		result.Issues.Should().BeEmpty();
		result.Chunks.Should().HaveCount(2);
		result.Chunks[0].ContentMarkdown.Should().Be("# Title");
		result.Chunks[1].ContentMarkdown.Should().Be("Body paragraph.");

		var heading = result.Chunks[0].SpanAnchor.AsObject();
		heading["type"]!.GetValue<string>().Should().Be("docx");
		heading["paragraphIndex"]!.GetValue<int>().Should().Be(0);
		heading["headingLevel"]!.GetValue<int>().Should().Be(1);

		var body = result.Chunks[1].SpanAnchor.AsObject();
		body["paragraphIndex"]!.GetValue<int>().Should().Be(1);
		body.ContainsKey("headingLevel").Should().BeFalse();
	}

	[Fact]
	public async Task CanonicalizeAsync_renders_tables_as_markdown_chunks()
	{
		var skill = new DocxSkill();
		await using var stream = BuildTableDocx();

		var result = await skill.CanonicalizeAsync(
			stream,
			new SourceMetadata("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "doc.docx"),
			CancellationToken.None);

		result.Issues.Should().BeEmpty();
		result.Chunks.Should().NotBeEmpty();

		var tableChunk = result.Chunks
			.FirstOrDefault(c => c.SpanAnchor.AsObject()["element"]?.GetValue<string>() == "table");
		tableChunk.Should().NotBeNull("table elements must produce their own chunk");
		tableChunk!.ContentMarkdown.Should().Contain("| name | value |");
		tableChunk.ContentMarkdown.Should().Contain("| --- | --- |");
		tableChunk.ContentMarkdown.Should().Contain("| alice | 1 |");
	}

	private static MemoryStream BuildHelloDocx()
	{
		var ms = new MemoryStream();
		using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
		{
			var main = doc.AddMainDocumentPart();
			var heading = new Paragraph(
				new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
				new Run(new Text("Title")));
			var body = new Paragraph(new Run(new Text("Body paragraph.")));
			main.Document = new Document(new Body(heading, body));
		}

		ms.Position = 0;
		return ms;
	}

	private static MemoryStream BuildTableDocx()
	{
		var ms = new MemoryStream();
		using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
		{
			var main = doc.AddMainDocumentPart();

			var headerRow = new TableRow(
				BuildTableCell("name"),
				BuildTableCell("value"));
			var dataRow = new TableRow(
				BuildTableCell("alice"),
				BuildTableCell("1"));
			var table = new Table(headerRow, dataRow);

			var pre = new Paragraph(new Run(new Text("Before table.")));
			main.Document = new Document(new Body(pre, table));
		}

		ms.Position = 0;
		return ms;

		static TableCell BuildTableCell(string text)
			=> new(new Paragraph(new Run(new Text(text))));
	}
}
