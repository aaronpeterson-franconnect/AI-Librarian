using System.Text;
using System.Text.RegularExpressions;

using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Wiki;

namespace AiLibrarian.WikiMaintainer;

/// <summary>
/// Deterministic Pass 2 per ADR 0007. Takes LLM-produced prose
/// containing inline citation tokens like <c>[chunk:abc-...-xyz]</c>
/// and splits it into atomic claims + per-claim citations.
///
/// <para>Pass 2 is NOT an LLM call. The algorithm:</para>
/// <list type="number">
///   <item>Find every <c>[chunk:GUID]</c> token in the prose with its
///         start/end character offsets.</item>
///   <item>Walk the prose sentence-by-sentence, treating <c>.</c>,
///         <c>!</c>, <c>?</c> followed by whitespace + an uppercase
///         letter (or a markdown heading) as a sentence boundary.
///         Citation tokens between the terminator and the next
///         sentence start stay attached to the prior sentence.</item>
///   <item>For each sentence range, strip the citation tokens from
///         the rendered text and emit a <see cref="WikiClaimDraft"/>
///         with the resolved citations.</item>
///   <item>Confidence + span are stamped from
///         <see cref="WikiMaintainerOptions"/> as placeholders until
///         embedding-similarity scoring lands.</item>
/// </list>
///
/// <para>Citations to chunks not in the supplied source pool are
/// dropped with a warning — the validator's rule 1 then catches the
/// resulting empty-citation claim.</para>
/// </summary>
public sealed class Pass2CitationExtractor
{
	private static readonly Regex CitationToken = new(
		"\\[chunk:(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\\]",
		RegexOptions.Compiled,
		TimeSpan.FromMilliseconds(200));

	private readonly WikiMaintainerOptions _options;

	/// <summary>Creates the extractor.</summary>
	public Pass2CitationExtractor(WikiMaintainerOptions options)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
	}

	/// <summary>Run Pass 2 over the LLM's prose.</summary>
	public Pass2Result Extract(
		string prose,
		IReadOnlyList<WikiMaintenanceSourceChunk> sourcePool,
		out List<string> warnings)
	{
		ArgumentNullException.ThrowIfNull(prose);
		ArgumentNullException.ThrowIfNull(sourcePool);
		warnings = new List<string>();

		if (string.IsNullOrWhiteSpace(prose))
		{
			return new Pass2Result(string.Empty, Array.Empty<WikiClaimDraft>());
		}

		var poolById = sourcePool.ToDictionary(c => c.ChunkId, c => c);

		var tokens = CitationToken.Matches(prose)
			.Select(m => new TokenSpan(
				Start: m.Index,
				End: m.Index + m.Length,
				ChunkId: Guid.TryParse(m.Groups["id"].Value, out var g) ? g : Guid.Empty))
			.Where(t => t.ChunkId != Guid.Empty)
			.ToList();

		var sentenceRanges = SplitSentenceRanges(prose);

		var claims = new List<WikiClaimDraft>();
		var renderedBody = new StringBuilder();
		var position = 0;

		foreach (var range in sentenceRanges)
		{
			var rawSegment = prose[range.Start..range.End];
			var citations = AttachCitationsInRange(range, tokens, poolById, warnings);

			var cleanText = CitationToken.Replace(rawSegment, string.Empty);
			cleanText = Regex.Replace(cleanText, "\\s{2,}", " ").Trim();
			if (cleanText.Length == 0)
			{
				continue;
			}

			if (citations.Count == 0)
			{
				warnings.Add($"claim {position} (\"{Snippet(cleanText)}\") has no [chunk:...] citation token");
			}

			claims.Add(new WikiClaimDraft(
				ClaimText: cleanText,
				Position: position,
				Citations: citations));

			if (renderedBody.Length > 0)
			{
				renderedBody.Append("\n\n");
			}

			renderedBody.Append(cleanText);
			position++;
		}

		return new Pass2Result(
			BodyMarkdown: renderedBody.ToString(),
			Claims: claims);
	}

	private static List<TextRange> SplitSentenceRanges(string prose)
	{
		var ranges = new List<TextRange>();
		var i = 0;
		var sentenceStart = 0;

		while (i < prose.Length)
		{
			var c = prose[i];
			var isTerminator = c == '.' || c == '!' || c == '?';
			var isParaBreak = c == '\n' && i + 1 < prose.Length && prose[i + 1] == '\n';
			// Markdown heading boundary: a single `\n` ending a line
			// that started with `#`. Headings are their own claim.
			var isHeadingLineBreak = c == '\n' && !isParaBreak && LineStartsWithHash(prose, sentenceStart);

			if (isParaBreak)
			{
				if (i > sentenceStart)
				{
					ranges.Add(new TextRange(sentenceStart, i));
				}

				// Skip the run of blank lines.
				while (i < prose.Length && prose[i] == '\n')
				{
					i++;
				}

				sentenceStart = i;
				continue;
			}

			if (isHeadingLineBreak)
			{
				ranges.Add(new TextRange(sentenceStart, i));
				sentenceStart = i + 1;
				i = sentenceStart;
				continue;
			}

			if (!isTerminator)
			{
				i++;
				continue;
			}

			// Candidate sentence boundary at i (the terminator). Walk
			// past optional whitespace + citation-token clusters to see
			// whether the NEXT character starts a new sentence.
			var afterBoundary = i + 1;
			while (afterBoundary < prose.Length)
			{
				while (afterBoundary < prose.Length && IsHorizontalWhitespace(prose[afterBoundary]))
				{
					afterBoundary++;
				}

				if (afterBoundary + 7 <= prose.Length
					&& prose.AsSpan(afterBoundary, 7).SequenceEqual("[chunk:".AsSpan()))
				{
					var close = prose.IndexOf(']', afterBoundary);
					if (close < 0)
					{
						break;
					}

					afterBoundary = close + 1;
					continue;
				}

				break;
			}

			var nextCh = afterBoundary < prose.Length ? prose[afterBoundary] : '\0';
			var nextStartsNewSentence = nextCh == '\0'
				|| nextCh == '#'
				|| char.IsUpper(nextCh)
				|| nextCh == '\n';

			if (nextStartsNewSentence)
			{
				ranges.Add(new TextRange(sentenceStart, afterBoundary));
				sentenceStart = afterBoundary;
				i = afterBoundary;
				continue;
			}

			i++;
		}

		if (sentenceStart < prose.Length)
		{
			ranges.Add(new TextRange(sentenceStart, prose.Length));
		}

		return ranges;
	}

	private static bool IsHorizontalWhitespace(char c) => c == ' ' || c == '\t';

	private static bool LineStartsWithHash(string prose, int lineStart)
	{
		var i = lineStart;
		while (i < prose.Length && IsHorizontalWhitespace(prose[i]))
		{
			i++;
		}

		return i < prose.Length && prose[i] == '#';
	}

	private List<Citation> AttachCitationsInRange(
		TextRange range,
		List<TokenSpan> tokens,
		Dictionary<Guid, WikiMaintenanceSourceChunk> sourcePool,
		List<string> warnings)
	{
		var citations = new List<Citation>();
		var seenChunkIds = new HashSet<Guid>();

		foreach (var token in tokens)
		{
			if (token.Start < range.Start || token.End > range.End)
			{
				continue;
			}

			if (!sourcePool.TryGetValue(token.ChunkId, out var chunk))
			{
				warnings.Add($"citation [chunk:{token.ChunkId:D}] points at a chunk that is NOT in the source pool; dropped");
				continue;
			}

			if (!seenChunkIds.Add(token.ChunkId))
			{
				continue;
			}

			var chunkLen = chunk.ContentMarkdown?.Length ?? 0;
			var spanStart = 0;
			var spanEnd = Math.Min(chunkLen, _options.DefaultCitationSpanLength);
			if (spanEnd <= spanStart)
			{
				spanStart = 0;
				spanEnd = Math.Max(1, chunkLen);
			}

			citations.Add(new Citation(
				Id: Guid.NewGuid(),
				ChunkId: token.ChunkId,
				SpanStart: spanStart,
				SpanEnd: spanEnd,
				Confidence: _options.DefaultCitationConfidence));
		}

		return citations;
	}

	private static string Snippet(string text)
		=> text.Length <= 60 ? text : text[..60] + "…";

	private readonly record struct TextRange(int Start, int End);

	private readonly record struct TokenSpan(int Start, int End, Guid ChunkId);
}

/// <summary>Outcome of one <see cref="Pass2CitationExtractor.Extract"/> call.</summary>
public sealed record Pass2Result(
	string BodyMarkdown,
	IReadOnlyList<WikiClaimDraft> Claims);
