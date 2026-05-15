using System.Text;
using System.Text.Json.Nodes;

using AiLibrarian.Domain.Skills;

using DocumentFormat.OpenXml.Packaging;

using D = DocumentFormat.OpenXml.Drawing;

namespace AiLibrarian.Skills.Office;

/// <summary>
/// PPTX canonicalizer — one chunk per slide, slide body text only.
/// Speaker notes pickup is deferred to a follow-up; for the
/// Engineering pilot the slide body alone covers the architecture-
/// review and design-deck content the corpus tends to carry. Speaker
/// notes will land as a second chunk per slide once the
/// <c>NotesSlidePart</c> traversal is wired.
/// </summary>
public sealed class PptxSkill : ISkill
{
	/// <inheritdoc />
	public string Name => "pptx";

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedMimeTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"application/vnd.openxmlformats-officedocument.presentationml.presentation",
	};

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".pptx",
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

		using var doc = PresentationDocument.Open(raw, isEditable: false);
		var presentationPart = doc.PresentationPart;
		if (presentationPart is null)
		{
			issues.Add(new SkillIssue(SkillIssueSeverity.Warning, "PPTX has no presentation part.", "pptx.empty_presentation"));
			return Task.FromResult(new SkillResult(string.Empty, chunks, EmptyMetadata(), issues));
		}

		var slideParts = presentationPart.SlideParts.ToList();
		var slideNumber = 0;
		foreach (var slidePart in slideParts)
		{
			cancellationToken.ThrowIfCancellationRequested();
			slideNumber++;

			var rendered = RenderSlide(slidePart, slideNumber);
			if (string.IsNullOrWhiteSpace(rendered))
			{
				continue;
			}

			canonical.Append(rendered).Append("\n\n");
			chunks.Add(new Chunk(rendered, BuildSpan(slideNumber), slideNumber - 1));
		}

		if (chunks.Count == 0)
		{
			issues.Add(new SkillIssue(SkillIssueSeverity.Warning, "PPTX produced no chunks (no readable slide text).", "pptx.no_chunks"));
		}

		return Task.FromResult(new SkillResult(
			CanonicalMarkdown: canonical.ToString().TrimEnd(),
			Chunks: chunks,
			ExtractedMetadata: EmptyMetadata(),
			Issues: issues));
	}

	private static Dictionary<string, JsonNode> EmptyMetadata()
		=> new(StringComparer.OrdinalIgnoreCase);

	private static string RenderSlide(SlidePart slidePart, int slideNumber)
	{
		var slide = slidePart.Slide;
		if (slide is null)
		{
			return string.Empty;
		}

		var sb = new StringBuilder();
		sb.Append("## Slide ").Append(slideNumber).Append("\n\n");

		var anyText = false;
		foreach (var paragraph in slide.Descendants<D.Paragraph>())
		{
			var line = string.Concat(paragraph.Descendants<D.Text>().Select(t => t.Text));
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			sb.Append(line.Trim()).Append('\n');
			anyText = true;
		}

		return anyText ? sb.ToString().TrimEnd() : string.Empty;
	}

	private static JsonObject BuildSpan(int slideNumber)
	{
		return new JsonObject
		{
			["type"] = "pptx",
			["slide"] = slideNumber,
		};
	}
}
