using System.IO;
using System.Text;
using System.Text.Json.Nodes;

using AiLibrarian.Domain.Skills;

namespace AiLibrarian.Skills.Markdown;

/// <summary>
/// Markdown ingest — pass-through with optional YAML frontmatter capture (raw string) and paragraph-grouped chunks per ADR 0009.
/// </summary>
public sealed class MarkdownSkill : ISkill
{
	private static readonly string[] ParagraphSeparators = ["\r\n\r\n", "\n\n"];

	/// <inheritdoc />
	public string Name => "markdown";

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedMimeTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"text/markdown",
		"text/x-markdown",
	};

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".md",
		".markdown",
	};

	/// <inheritdoc />
	public async Task<SkillResult> CanonicalizeAsync(Stream raw, SourceMetadata metadata, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(raw);
		ArgumentNullException.ThrowIfNull(metadata);

		using var reader = new StreamReader(raw, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
		var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

		var (extracted, body) = TrySplitFrontmatter(text);
		var canonical = body.TrimEnd();
		var issues = new List<SkillIssue>();

		if (string.IsNullOrWhiteSpace(canonical))
		{
			issues.Add(new SkillIssue(SkillIssueSeverity.Warning, "Markdown body is empty after frontmatter removal.", "markdown.empty_body"));
		}

		var chunks = BuildParagraphChunks(canonical, issues);
		return new SkillResult(canonical, chunks, extracted, issues);
	}

	private static (IReadOnlyDictionary<string, JsonNode> Meta, string Body) TrySplitFrontmatter(string text)
	{
		var meta = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
		using var sr = new StringReader(text);
		var first = sr.ReadLine();
		if (first is null || first.Trim() != "---")
		{
			return (meta, text);
		}

		var fmLines = new List<string>();
		while (true)
		{
			var line = sr.ReadLine();
			if (line is null)
			{
				return (meta, text);
			}

			if (line.Trim() == "---")
			{
				break;
			}

			fmLines.Add(line);
		}

		meta["frontmatter_raw"] = JsonValue.Create(string.Join('\n', fmLines));
		var body = sr.ReadToEnd();
		return (meta, body);
	}

	private static List<Chunk> BuildParagraphChunks(string body, List<SkillIssue> issues)
	{
		if (string.IsNullOrEmpty(body))
		{
			return [new Chunk(string.Empty, MarkdownSpan(0), 0)];
		}

		var segments = body.Split(ParagraphSeparators, StringSplitOptions.None);
		var parts = new List<string>();
		foreach (var seg in segments)
		{
			var t = seg.Trim();
			if (t.Length > 0)
			{
				parts.Add(t);
			}
		}

		if (parts.Count == 0)
		{
			var trimmed = body.Trim();
			if (trimmed.Length > 0)
			{
				parts.Add(trimmed);
			}
			else
			{
				return [new Chunk(string.Empty, MarkdownSpan(0), 0)];
			}
		}

		var chunks = new List<Chunk>(parts.Count);
		for (var i = 0; i < parts.Count; i++)
		{
			chunks.Add(new Chunk(parts[i], MarkdownSpan(i), i));
		}

		if (chunks.Count > 200)
		{
			issues.Add(new SkillIssue(SkillIssueSeverity.Info, $"Split into {chunks.Count} paragraph chunks.", "markdown.chunk_count"));
		}

		return chunks;
	}

	private static JsonObject MarkdownSpan(int paragraphIndex)
	{
		return new JsonObject
		{
			["type"] = "markdown",
			["paragraphIndex"] = paragraphIndex,
		};
	}
}
