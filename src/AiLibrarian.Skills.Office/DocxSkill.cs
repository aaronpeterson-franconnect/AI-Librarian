using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using AiLibrarian.Domain.Skills;

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AiLibrarian.Skills.Office;

/// <summary>
/// DOCX canonicalizer — paragraph-grouped chunks with heading
/// recognition. Heading detection looks at <c>w:pPr/w:pStyle</c> with
/// the canonical Word heading style names (<c>Heading1</c> .. <c>Heading6</c>);
/// detected headings are emitted as <c># Title</c> through <c>###### Title</c>
/// in the canonical markdown so retrieval can use them as natural
/// section markers without parsing Word's run-level formatting.
/// </summary>
public sealed class DocxSkill : ISkill
{
	/// <inheritdoc />
	public string Name => "docx";

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedMimeTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"application/vnd.openxmlformats-officedocument.wordprocessingml.document",
	};

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".docx",
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

		using var doc = WordprocessingDocument.Open(raw, isEditable: false);
		var body = doc.MainDocumentPart?.Document.Body;
		if (body is null)
		{
			issues.Add(new SkillIssue(SkillIssueSeverity.Warning, "DOCX has no main document body.", "docx.empty_body"));
			return Task.FromResult(new SkillResult(string.Empty, chunks, EmptyMetadata(), issues));
		}

		var paragraphIndex = 0;
		foreach (var para in body.Elements<Paragraph>())
		{
			cancellationToken.ThrowIfCancellationRequested();

			var headingLevel = TryGetHeadingLevel(para);
			var text = ExtractParagraphText(para);
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}

			var rendered = headingLevel is { } level
				? string.Concat(new string('#', level), " ", text.Trim())
				: text.Trim();

			canonical.Append(rendered).Append("\n\n");
			chunks.Add(new Chunk(rendered, BuildSpan(paragraphIndex, headingLevel), paragraphIndex));
			paragraphIndex++;
		}

		// Tables — flatten to one chunk per table with markdown rows.
		foreach (var table in body.Elements<Table>())
		{
			cancellationToken.ThrowIfCancellationRequested();

			var rendered = RenderTable(table);
			if (string.IsNullOrWhiteSpace(rendered))
			{
				continue;
			}

			canonical.Append(rendered).Append("\n\n");
			chunks.Add(new Chunk(rendered, BuildTableSpan(paragraphIndex), paragraphIndex));
			paragraphIndex++;
		}

		if (chunks.Count == 0)
		{
			issues.Add(new SkillIssue(SkillIssueSeverity.Warning, "DOCX produced no chunks (empty paragraphs and tables).", "docx.no_chunks"));
		}

		return Task.FromResult(new SkillResult(
			CanonicalMarkdown: canonical.ToString().TrimEnd(),
			Chunks: chunks,
			ExtractedMetadata: EmptyMetadata(),
			Issues: issues));
	}

	private static Dictionary<string, JsonNode> EmptyMetadata()
		=> new(StringComparer.OrdinalIgnoreCase);

	private static int? TryGetHeadingLevel(Paragraph para)
	{
		var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
		if (string.IsNullOrEmpty(styleId))
		{
			return null;
		}

		// Word's default heading styles are "Heading1".."Heading9"; some
		// templates use "heading 1" etc. Accept both shapes.
		var normalized = styleId.Replace(" ", string.Empty, StringComparison.Ordinal);
		if (!normalized.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var tail = normalized.AsSpan("Heading".Length);
		if (tail.Length == 0 || !int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
		{
			return null;
		}

		return Math.Clamp(level, 1, 6);
	}

	private static string ExtractParagraphText(Paragraph para)
	{
		var sb = new StringBuilder();
		foreach (var run in para.Descendants<Run>())
		{
			foreach (var text in run.Elements<Text>())
			{
				sb.Append(text.Text);
			}

			if (run.Elements<Break>().Any() || run.Elements<TabChar>().Any())
			{
				sb.Append(' ');
			}
		}

		return sb.ToString();
	}

	private static string RenderTable(Table table)
	{
		var rows = table.Elements<TableRow>().ToList();
		if (rows.Count == 0)
		{
			return string.Empty;
		}

		var sb = new StringBuilder();
		var headerWritten = false;
		var columnCount = 0;

		foreach (var row in rows)
		{
			var cells = row.Elements<TableCell>()
				.Select(c => string.Join(' ', c.Elements<Paragraph>().Select(ExtractParagraphText)).Trim().Replace("|", "\\|", StringComparison.Ordinal))
				.ToList();

			if (cells.Count == 0)
			{
				continue;
			}

			columnCount = Math.Max(columnCount, cells.Count);
			sb.Append("| ").AppendJoin(" | ", cells).Append(" |").Append('\n');

			if (!headerWritten)
			{
				sb.Append('|');
				for (var i = 0; i < cells.Count; i++)
				{
					sb.Append(" --- |");
				}

				sb.Append('\n');
				headerWritten = true;
			}
		}

		return columnCount == 0 ? string.Empty : sb.ToString().TrimEnd();
	}

	private static JsonObject BuildSpan(int paragraphIndex, int? headingLevel)
	{
		var span = new JsonObject
		{
			["type"] = "docx",
			["paragraphIndex"] = paragraphIndex,
		};

		if (headingLevel.HasValue)
		{
			span["headingLevel"] = headingLevel.Value;
		}

		return span;
	}

	private static JsonObject BuildTableSpan(int paragraphIndex)
	{
		return new JsonObject
		{
			["type"] = "docx",
			["paragraphIndex"] = paragraphIndex,
			["element"] = "table",
		};
	}
}
