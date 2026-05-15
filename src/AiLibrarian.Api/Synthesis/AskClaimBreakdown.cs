using System.Text.RegularExpressions;

namespace AiLibrarian.Api.Synthesis;

/// <summary>
/// Lightweight post-hoc parser that splits an LLM-produced answer
/// into per-sentence claims and surfaces inline <c>[chunk:GUID]</c>
/// citation tokens. Used by the <c>/api/ask</c> response shape so
/// callers (the eval harness, the librarian dashboard) can compute
/// citation coverage without re-running the synthesis.
///
/// <para><b>Best-effort, not a contract.</b> The AskGuard's system
/// prompt may or may not instruct the model to emit inline citation
/// tokens; this parser does the right thing in either case. When no
/// tokens are present, every sentence becomes a claim with zero
/// citations and citation-coverage drops to 0 — that's the signal
/// to either tighten the prompt or accept that the surface doesn't
/// cite.</para>
///
/// <para>Distinct from <see cref="WikiMaintainer.Pass2CitationExtractor"/>:
/// that one is the structural Pass 2 of the Wiki Maintainer pipeline
/// and emits <c>WikiClaimDraft</c> records bound for the citation
/// validator. The breakdown here is response-shape sugar only — no
/// validation, no source-pool resolution, no chunk-existence check.
/// Re-using the regex is intentional; the parsing model is the same.</para>
/// </summary>
public static partial class AskClaimBreakdown
{
	[GeneratedRegex(
		@"\[chunk:(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\]",
		RegexOptions.Compiled,
		matchTimeoutMilliseconds: 200)]
	private static partial Regex CitationToken();

	/// <summary>
	/// Sentence-terminator regex: <c>.</c>, <c>!</c>, or <c>?</c>
	/// followed by whitespace. Matches the conservative splitter used
	/// in Pass2 — over-splitting (e.g. on abbreviations like "e.g.")
	/// is preferred to under-splitting because the metric counts
	/// claims per cited chunk-id, which is robust to extra splits.
	/// </summary>
	[GeneratedRegex(@"(?<=[.!?])\s+(?=[A-ZÀ-ſ])", RegexOptions.Compiled)]
	private static partial Regex SentenceBoundary();

	/// <summary>Parse <paramref name="answer"/> into claims + aggregate counts.</summary>
	public static AskClaimBreakdownResult Extract(string? answer)
	{
		if (string.IsNullOrWhiteSpace(answer))
		{
			return new AskClaimBreakdownResult(
				Claims: Array.Empty<AskClaim>(),
				ClaimCount: 0,
				CitedClaimCount: 0);
		}

		var sentences = SentenceBoundary().Split(answer);
		var claims = new List<AskClaim>(sentences.Length);
		var citedCount = 0;

		foreach (var raw in sentences)
		{
			var trimmed = raw.Trim();
			if (trimmed.Length == 0)
			{
				continue;
			}

			var matches = CitationToken().Matches(trimmed);
			var chunkIds = new List<Guid>(matches.Count);
			foreach (Match m in matches)
			{
				if (Guid.TryParse(m.Groups["id"].Value, out var g))
				{
					chunkIds.Add(g);
				}
			}

			// Render text with citation tokens stripped for the per-claim
			// surface; the raw tokens leak implementation detail.
			var rendered = CitationToken().Replace(trimmed, string.Empty).Trim();
			if (rendered.Length == 0)
			{
				// Sentence was only citation tokens; skip.
				continue;
			}

			var hasCitation = chunkIds.Count > 0;
			if (hasCitation)
			{
				citedCount++;
			}

			claims.Add(new AskClaim(rendered, chunkIds));
		}

		return new AskClaimBreakdownResult(
			Claims: claims,
			ClaimCount: claims.Count,
			CitedClaimCount: citedCount);
	}
}

/// <summary>Per-sentence claim row in the <c>/api/ask</c> response.</summary>
/// <param name="Text">Sentence text with citation tokens stripped.</param>
/// <param name="ChunkIds">Distinct chunk ids cited inline; empty when the sentence carries no <c>[chunk:GUID]</c> tokens.</param>
public sealed record AskClaim(string Text, IReadOnlyList<Guid> ChunkIds);

/// <summary>Aggregate shape returned by <see cref="AskClaimBreakdown.Extract"/>.</summary>
/// <param name="Claims">Per-sentence rows in document order.</param>
/// <param name="ClaimCount">Total sentences parsed (equal to <c>Claims.Count</c>).</param>
/// <param name="CitedClaimCount">Sentences with at least one inline citation token.</param>
public sealed record AskClaimBreakdownResult(
	IReadOnlyList<AskClaim> Claims,
	int ClaimCount,
	int CitedClaimCount);
