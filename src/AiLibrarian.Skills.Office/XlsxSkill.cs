using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using AiLibrarian.Domain.Skills;

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AiLibrarian.Skills.Office;

/// <summary>
/// XLSX canonicalizer — one chunk per worksheet rendered as a markdown
/// table. Phase 1 keeps the chunking coarse on purpose: a spreadsheet
/// is usually meaningful at the sheet level, and per-row chunking
/// would explode embedding cost on large workbooks. Sheet-level
/// chunks fit well within a 2k-token retrieval window for the
/// runbook-sized spreadsheets the Engineering pilot deals with;
/// Phase 2 can refine for large data files.
/// </summary>
public sealed class XlsxSkill : ISkill
{
	/// <inheritdoc />
	public string Name => "xlsx";

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedMimeTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
	};

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".xlsx",
	};

	/// <inheritdoc />
	public Task<SkillResult> CanonicalizeAsync(Stream raw, SourceMetadata metadata, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(raw);
		ArgumentNullException.ThrowIfNull(metadata);
		cancellationToken.ThrowIfCancellationRequested();

		var issues = new List<SkillIssue>();
		var chunks = new List<Chunk>();
		var canonical = new StringBuilder();

		using var doc = SpreadsheetDocument.Open(raw, isEditable: false);
		var workbookPart = doc.WorkbookPart;
		if (workbookPart is null)
		{
			issues.Add(new SkillIssue(SkillIssueSeverity.Warning, "XLSX has no workbook part.", "xlsx.empty_workbook"));
			return Task.FromResult(new SkillResult(string.Empty, chunks, EmptyMetadata(), issues));
		}

		var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
		var sheets = workbookPart.Workbook.Descendants<Sheet>().ToList();

		var sheetIndex = 0;
		foreach (var sheet in sheets)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (sheet.Id?.Value is not { } relId)
			{
				sheetIndex++;
				continue;
			}

			if (workbookPart.GetPartById(relId) is not WorksheetPart wsPart)
			{
				sheetIndex++;
				continue;
			}

			var sheetName = sheet.Name?.Value ?? $"Sheet{sheetIndex + 1}";
			var rendered = RenderWorksheet(sheetName, wsPart, sharedStrings);
			if (string.IsNullOrWhiteSpace(rendered))
			{
				sheetIndex++;
				continue;
			}

			canonical.Append(rendered).Append("\n\n");
			chunks.Add(new Chunk(rendered, BuildSpan(sheetName, sheetIndex), sheetIndex));
			sheetIndex++;
		}

		if (chunks.Count == 0)
		{
			issues.Add(new SkillIssue(SkillIssueSeverity.Warning, "XLSX produced no chunks (empty workbook).", "xlsx.no_chunks"));
		}

		return Task.FromResult(new SkillResult(
			CanonicalMarkdown: canonical.ToString().TrimEnd(),
			Chunks: chunks,
			ExtractedMetadata: EmptyMetadata(),
			Issues: issues));
	}

	private static Dictionary<string, JsonNode> EmptyMetadata()
		=> new(StringComparer.OrdinalIgnoreCase);

	private static string RenderWorksheet(string sheetName, WorksheetPart wsPart, SharedStringTable? sharedStrings)
	{
		var rows = wsPart.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
		if (rows.Count == 0)
		{
			return string.Empty;
		}

		var sb = new StringBuilder();
		sb.Append("## Sheet: ").Append(sheetName).Append("\n\n");

		var maxCols = 0;
		var rendered = new List<List<string>>(rows.Count);
		foreach (var row in rows)
		{
			var cells = row.Elements<Cell>()
				.Select(c => ResolveCellText(c, sharedStrings).Replace("|", "\\|", StringComparison.Ordinal))
				.ToList();
			rendered.Add(cells);
			maxCols = Math.Max(maxCols, cells.Count);
		}

		if (maxCols == 0)
		{
			return string.Empty;
		}

		// Pad every row to maxCols so the markdown table is rectangular.
		for (var rowIndex = 0; rowIndex < rendered.Count; rowIndex++)
		{
			var row = rendered[rowIndex];
			while (row.Count < maxCols)
			{
				row.Add(string.Empty);
			}

			sb.Append("| ").AppendJoin(" | ", row).Append(" |").Append('\n');

			if (rowIndex == 0)
			{
				sb.Append('|');
				for (var i = 0; i < maxCols; i++)
				{
					sb.Append(" --- |");
				}

				sb.Append('\n');
			}
		}

		return sb.ToString().TrimEnd();
	}

	private static string ResolveCellText(Cell cell, SharedStringTable? sharedStrings)
	{
		var raw = cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
		if (cell.DataType?.Value == CellValues.SharedString
			&& sharedStrings is not null
			&& int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
		{
			var item = sharedStrings.ElementAtOrDefault(index);
			if (item is not null)
			{
				return item.InnerText;
			}
		}

		if (cell.DataType?.Value == CellValues.Boolean)
		{
			return raw == "1" ? "TRUE" : "FALSE";
		}

		return raw;
	}

	private static JsonObject BuildSpan(string sheetName, int sheetIndex)
	{
		return new JsonObject
		{
			["type"] = "xlsx",
			["sheet"] = sheetName,
			["sheetIndex"] = sheetIndex,
		};
	}
}
