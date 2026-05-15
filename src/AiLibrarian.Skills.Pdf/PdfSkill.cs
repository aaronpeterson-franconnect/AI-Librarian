using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AiLibrarian.Domain.Skills;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace AiLibrarian.Skills.Pdf;

/// <summary>
/// PDF canonicalizer — page-grouped chunks with whitespace
/// normalization. Reading order comes from PdfPig's
/// <see cref="ContentOrderTextExtractor"/>; pages with no extracted
/// text (scanned PDFs without OCR, image-only pages) emit one
/// <see cref="SkillIssueSeverity.Warning"/> per occurrence so the
/// operator sees the gap instead of staring at a healthy-but-empty
/// source.
/// </summary>
public sealed class PdfSkill : ISkill
{
	private static readonly Regex CollapseSpaces = new("[ \\t]+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
	private static readonly Regex CollapseBlankLines = new("\\n{3,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
	private static readonly Regex HardLineBreak = new("(?<!\\n)\\n(?!\\n)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

	/// <inheritdoc />
	public string Name => "pdf";

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedMimeTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"application/pdf",
		"application/x-pdf",
	};

	/// <inheritdoc />
	public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".pdf",
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

		// PdfPig needs a seekable stream. If the caller handed us a
		// forward-only one (HTTP response, Service Bus body), copy to
		// memory first — PDFs in the pilot are bounded by the API's
		// upload size limit so the memory cost is bounded.
		using var seekable = EnsureSeekable(raw);

		PdfDocument document;
		try
		{
			document = PdfDocument.Open(seekable);
		}
		catch (PdfDocumentEncryptedException ex)
		{
			issues.Add(new SkillIssue(
				SkillIssueSeverity.Error,
				$"PDF is encrypted and could not be opened with an empty password: {ex.Message}",
				"pdf.encrypted"));
			return Task.FromResult(new SkillResult(string.Empty, chunks, EmptyMetadata(), issues));
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
		{
			issues.Add(new SkillIssue(
				SkillIssueSeverity.Error,
				$"Failed to parse PDF: {ex.Message}",
				"pdf.parse_failed"));
			return Task.FromResult(new SkillResult(string.Empty, chunks, EmptyMetadata(), issues));
		}

		using (document)
		{
			var chunkIndex = 0;
			foreach (var page in document.GetPages())
			{
				cancellationToken.ThrowIfCancellationRequested();

				var pageNumber = page.Number;
				var pageText = ExtractPageText(page);
				if (string.IsNullOrWhiteSpace(pageText))
				{
					issues.Add(new SkillIssue(
						SkillIssueSeverity.Warning,
						$"Page {pageNumber.ToString(CultureInfo.InvariantCulture)} produced no text (likely scanned/image-only; needs OCR).",
						"pdf.no_text"));
					chunkIndex++;
					continue;
				}

				var rendered = $"## Page {pageNumber.ToString(CultureInfo.InvariantCulture)}\n\n{pageText.Trim()}";
				canonical.Append(rendered).Append("\n\n");
				chunks.Add(new Chunk(rendered, BuildSpan(pageNumber), chunkIndex));
				chunkIndex++;
			}

			if (chunks.Count == 0 && issues.Count == 0)
			{
				issues.Add(new SkillIssue(SkillIssueSeverity.Warning, "PDF produced no chunks (empty document).", "pdf.no_chunks"));
			}

			var pageCount = document.NumberOfPages;
			var metadataDict = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase)
			{
				["page_count"] = JsonValue.Create(pageCount),
			};

			TryAddInfo(metadataDict, "title", document.Information?.Title);
			TryAddInfo(metadataDict, "author", document.Information?.Author);
			TryAddInfo(metadataDict, "subject", document.Information?.Subject);
			TryAddInfo(metadataDict, "producer", document.Information?.Producer);

			return Task.FromResult(new SkillResult(
				CanonicalMarkdown: canonical.ToString().TrimEnd(),
				Chunks: chunks,
				ExtractedMetadata: metadataDict,
				Issues: issues));
		}
	}

	private static Stream EnsureSeekable(Stream raw)
	{
		if (raw.CanSeek)
		{
			// Reset to 0 -- callers may have peeked.
			if (raw.Position != 0)
			{
				raw.Position = 0;
			}

			return new NonClosingStream(raw);
		}

		var memory = new MemoryStream();
		raw.CopyTo(memory);
		memory.Position = 0;
		return memory;
	}

	private static string ExtractPageText(Page page)
	{
		string text;
		try
		{
			text = ContentOrderTextExtractor.GetText(page);
		}
		catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException)
		{
			// Fallback to page.Text if the content-order extractor
			// can't handle this page's font/encoding combination.
			text = page.Text;
		}

		return Normalize(text);
	}

	internal static string Normalize(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}

		// Standardize line endings first so the regexes don't need to
		// branch on \r vs \r\n vs \n.
		var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace('\r', '\n');

		normalized = CollapseSpaces.Replace(normalized, " ");
		normalized = CollapseBlankLines.Replace(normalized, "\n\n");
		// Hard line-break in mid-paragraph: join with a space. Hyphenated
		// line-breaks ("docu-\nmentation") rejoin without the hyphen.
		normalized = Regex.Replace(normalized, "(?<word>\\w)-\\n(?=\\w)", "${word}");
		normalized = HardLineBreak.Replace(normalized, " ");

		return normalized.Trim();
	}

	private static void TryAddInfo(Dictionary<string, JsonNode> bag, string key, string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return;
		}

		bag[key] = JsonValue.Create(value.Trim())!;
	}

	private static Dictionary<string, JsonNode> EmptyMetadata()
		=> new(StringComparer.OrdinalIgnoreCase);

	private static JsonObject BuildSpan(int pageNumber)
		=> new()
		{
			["type"] = "pdf",
			["pageNumber"] = pageNumber,
		};

	/// <summary>
	/// PdfPig disposes the supplied stream when the document is
	/// disposed; callers (e.g. the ingest pipeline) expect to keep
	/// owning their input stream. This wrapper swallows close calls
	/// so the inner stream lives on.
	/// </summary>
	private sealed class NonClosingStream : Stream
	{
		private readonly Stream _inner;

		public NonClosingStream(Stream inner)
		{
			_inner = inner;
		}

		public override bool CanRead => _inner.CanRead;
		public override bool CanSeek => _inner.CanSeek;
		public override bool CanWrite => _inner.CanWrite;
		public override long Length => _inner.Length;
		public override long Position { get => _inner.Position; set => _inner.Position = value; }
		public override void Flush() => _inner.Flush();
		public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
		public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
		public override void SetLength(long value) => _inner.SetLength(value);
		public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

		protected override void Dispose(bool disposing)
		{
			// Deliberately do not dispose the inner stream -- caller owns it.
			base.Dispose(disposing);
		}
	}
}
