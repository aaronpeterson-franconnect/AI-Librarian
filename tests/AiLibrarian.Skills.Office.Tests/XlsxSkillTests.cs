using AiLibrarian.Domain.Skills;
using AiLibrarian.Skills.Office;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AiLibrarian.Skills.Office.Tests;

public sealed class XlsxSkillTests
{
	[Fact]
	public async Task CanonicalizeAsync_emits_one_chunk_per_sheet_as_markdown_table()
	{
		var skill = new XlsxSkill();
		await using var stream = BuildSimpleXlsx();

		var result = await skill.CanonicalizeAsync(
			stream,
			new SourceMetadata("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "data.xlsx"),
			CancellationToken.None);

		result.Issues.Should().BeEmpty();
		result.Chunks.Should().HaveCount(1);

		var span = result.Chunks[0].SpanAnchor.AsObject();
		span["type"]!.GetValue<string>().Should().Be("xlsx");
		span["sheet"]!.GetValue<string>().Should().Be("Runbook");
		span["sheetIndex"]!.GetValue<int>().Should().Be(0);

		var content = result.Chunks[0].ContentMarkdown;
		content.Should().Contain("## Sheet: Runbook");
		content.Should().Contain("| name | count |");
		content.Should().Contain("| --- | --- |");
		content.Should().Contain("| widgets | 42 |");
	}

	private static MemoryStream BuildSimpleXlsx()
	{
		var ms = new MemoryStream();
		using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, autoSave: true))
		{
			var wbPart = doc.AddWorkbookPart();
			wbPart.Workbook = new Workbook();

			var wsPart = wbPart.AddNewPart<WorksheetPart>();
			var sheetData = new SheetData(
				new Row(
					BuildInlineCell("A1", "name"),
					BuildInlineCell("B1", "count")),
				new Row(
					BuildInlineCell("A2", "widgets"),
					BuildNumberCell("B2", 42)));
			wsPart.Worksheet = new Worksheet(sheetData);

			var sheets = wbPart.Workbook.AppendChild(new Sheets());
			sheets.AppendChild(new Sheet
			{
				Id = wbPart.GetIdOfPart(wsPart),
				SheetId = 1U,
				Name = "Runbook",
			});

			wbPart.Workbook.Save();
		}

		ms.Position = 0;
		return ms;

		static Cell BuildInlineCell(string reference, string value)
			=> new()
			{
				CellReference = reference,
				DataType = CellValues.InlineString,
				InlineString = new InlineString(new Text(value)),
			};

		static Cell BuildNumberCell(string reference, double value)
			=> new()
			{
				CellReference = reference,
				CellValue = new CellValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
			};
	}
}
