namespace AiLibrarian.Domain.Citations;

/// <summary>
/// The five rules from ADR 0007 the <see cref="ICitationValidator"/>
/// enforces. Rule codes are stable strings so callers (CI, dashboards,
/// the eval harness) can pivot violations by rule without coupling to
/// the enum's ordinal value.
/// </summary>
public enum CitationRule
{
	/// <summary>Rule 1: every claim has at least one citation.</summary>
	ClaimHasCitation = 1,

	/// <summary>Rule 2: every citation resolves to a non-deleted chunk.</summary>
	ChunkResolves = 2,

	/// <summary>Rule 3: the citation span lies within the chunk's bounds.</summary>
	SpanWithinChunk = 3,

	/// <summary>
	/// Rule 4: <c>chunk.classification &lt;= facet.classification</c> —
	/// the citation cannot leak more-sensitive content upward.
	/// </summary>
	ClassificationNotLeaking = 4,

	/// <summary>Rule 5: <c>citation.confidence &gt;= configured floor</c>.</summary>
	ConfidenceFloorMet = 5,
}

/// <summary>Helpers for stable rule-code rendering.</summary>
public static class CitationRuleCodes
{
	/// <summary>Stable wire-format code for the rule (e.g. <c>R1.ClaimHasCitation</c>).</summary>
	public static string ToCode(this CitationRule rule)
	{
		return rule switch
		{
			CitationRule.ClaimHasCitation => "R1.ClaimHasCitation",
			CitationRule.ChunkResolves => "R2.ChunkResolves",
			CitationRule.SpanWithinChunk => "R3.SpanWithinChunk",
			CitationRule.ClassificationNotLeaking => "R4.ClassificationNotLeaking",
			CitationRule.ConfidenceFloorMet => "R5.ConfidenceFloorMet",
			_ => $"R{(int)rule}.Unknown",
		};
	}
}
