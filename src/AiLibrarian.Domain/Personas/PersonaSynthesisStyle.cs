namespace AiLibrarian.Domain.Personas;

/// <summary>
/// Persona-aware synthesis style per ADR 0015. Loaded from the
/// <c>personas.synthesis_style</c> JSONB column and applied as a
/// suffix to the LLM's system prompt. Every field has a documented
/// neutral default so a missing or partial profile yields the same
/// behavior as no profile.
///
/// <para><b>Hints, not contracts.</b> The structural rules the
/// pipeline enforces (every claim cites at least one chunk, citations
/// must resolve, classification ceilings hold) are <em>not</em>
/// configurable through this profile. The fields here only nudge the
/// LLM toward a tone / shape that suits the persona; the validator
/// keeps the ground truth.</para>
///
/// <para>v1 application: the Wiki Maintainer's Pass-1 system prompt
/// appends style-derived directives. Other synthesis paths (the MCP
/// <c>ask</c> tool when it lands) will pick up the same record.</para>
/// </summary>
/// <param name="AnswerLength">Hint at desired answer length.</param>
/// <param name="Structure">Hint at output structure (narrative prose, bullets, tabular layout, code-first).</param>
/// <param name="CitationDensity">Hint at citation cadence; the structural floor (one citation per claim) still applies regardless.</param>
/// <param name="CodeQuoting">Hint at how to quote code blocks.</param>
/// <param name="HedgingPosture">Hint at hedging tone.</param>
/// <param name="AbstentionThreshold">Per-persona confidence floor below which the synthesizer should prefer abstention over a hedged answer. v1 captures this for future ask/synthesize tools; the Wiki Maintainer reads it as a soft hint only.</param>
/// <param name="CrossSourceSynthesis">Hint at whether to blend multiple sources or keep claims grounded in a single one.</param>
/// <param name="ShowSourceMetadata">When true, the prompt asks the LLM to surface source titles / dates alongside claims.</param>
public sealed record PersonaSynthesisStyle(
	AnswerLengthHint AnswerLength,
	StructurePreference Structure,
	CitationDensity CitationDensity,
	CodeQuotingPreference CodeQuoting,
	HedgingPosture HedgingPosture,
	double AbstentionThreshold,
	CrossSourceSynthesisMode CrossSourceSynthesis,
	bool ShowSourceMetadata)
{
	/// <summary>The neutral defaults per ADR 0015's documented profile shape.</summary>
	public static PersonaSynthesisStyle Neutral { get; } = new(
		AnswerLength: AnswerLengthHint.Medium,
		// Note: AnswerLengthHint.Brief / .Extended correspond to JSON "short" / "long" per ADR 0015.
		Structure: StructurePreference.Narrative,
		CitationDensity: CitationDensity.PerClaim,
		CodeQuoting: CodeQuotingPreference.PreserveContext,
		HedgingPosture: HedgingPosture.Calibrated,
		AbstentionThreshold: 0.7,
		CrossSourceSynthesis: CrossSourceSynthesisMode.Always,
		ShowSourceMetadata: true);
}

/// <summary>Hint for the answer length the LLM should produce. JSONB values <c>"short"</c>, <c>"medium"</c>, <c>"long"</c> per ADR 0015 — the C# enum uses non-keyword names to dodge CA1720 type-name clashes.</summary>
public enum AnswerLengthHint
{
	/// <summary>Short, terse — typically a paragraph or less. JSON: <c>"short"</c>.</summary>
	Brief = 0,

	/// <summary>Default — a few paragraphs. JSON: <c>"medium"</c>.</summary>
	Medium = 1,

	/// <summary>Long-form — multiple sections, comprehensive coverage. JSON: <c>"long"</c>.</summary>
	Extended = 2,
}

/// <summary>Hint for output structure.</summary>
public enum StructurePreference
{
	/// <summary>Narrative prose (default).</summary>
	Narrative = 0,

	/// <summary>Bulleted list-of-points.</summary>
	Bullet = 1,

	/// <summary>Tabular layout (markdown tables).</summary>
	Tabular = 2,

	/// <summary>Code-first: code blocks are primary, prose around them is minimal.</summary>
	CodeFirst = 3,
}

/// <summary>How dense should citations be?</summary>
public enum CitationDensity
{
	/// <summary>One citation per claim (the structural floor; default).</summary>
	PerClaim = 0,

	/// <summary>One citation per paragraph — sentences within a paragraph may share the citation.</summary>
	PerParagraph = 1,

	/// <summary>Citations only where the source is non-obvious; tolerates whole paragraphs without inline tokens.</summary>
	Minimal = 2,
}

/// <summary>How to quote code blocks.</summary>
public enum CodeQuotingPreference
{
	/// <summary>Preserve surrounding context — show the function plus its caller.</summary>
	PreserveContext = 0,

	/// <summary>Quote only the relevant lines.</summary>
	Minimal = 1,

	/// <summary>Inline code only — avoid block quotes.</summary>
	Inline = 2,
}

/// <summary>Hedging tone.</summary>
public enum HedgingPosture
{
	/// <summary>Calibrated: hedge in proportion to evidence strength (default).</summary>
	Calibrated = 0,

	/// <summary>Conservative: hedge widely; prefer "I don't know" to overclaiming.</summary>
	Conservative = 1,

	/// <summary>Direct: state findings plainly; the citation contract carries the safety, not the hedging.</summary>
	Direct = 2,
}

/// <summary>How aggressively to blend multiple sources into a single claim.</summary>
public enum CrossSourceSynthesisMode
{
	/// <summary>Always blend when the sources agree (default).</summary>
	Always = 0,

	/// <summary>Blend only when one source is insufficient.</summary>
	WhenNeeded = 1,

	/// <summary>Keep claims pinned to a single source when possible.</summary>
	Minimal = 2,
}
