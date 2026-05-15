namespace AiLibrarian.WikiMaintainer;

/// <summary>
/// Knobs for the Wiki Maintainer. Defaults match the Phase 2 plan in
/// ADR 0006 + 0007; deployments tighten per tenant via the standard
/// options pipeline.
/// </summary>
public sealed class WikiMaintainerOptions
{
	/// <summary>Configuration section name (<c>WikiMaintainer</c>).</summary>
	public const string SectionName = "WikiMaintainer";

	/// <summary>LLM model identifier used for Pass 1 synthesis.</summary>
	public string Model { get; set; } = "gpt-4o-mini";

	/// <summary>Max tokens for the Pass 1 completion. Default 2048.</summary>
	public int MaxTokens { get; set; } = 2048;

	/// <summary>Sampling temperature. Default 0.2 — low for stable, citation-friendly prose.</summary>
	public double Temperature { get; set; } = 0.2;

	/// <summary>
	/// Per-citation confidence the Pass 2 extractor stamps on each
	/// row. Placeholder until embedding-similarity scoring lands;
	/// must be ≥ the validator's rule-5 floor (default 0.7) so
	/// extracted citations actually pass. Default 0.85.
	/// </summary>
	public double DefaultCitationConfidence { get; set; } = 0.85;

	/// <summary>
	/// Span-anchor character length per citation. The extractor emits
	/// <c>[span_start, span_end)</c> tuples against the cited chunk's
	/// canonical markdown; this default keeps them small + valid.
	/// Default 200.
	/// </summary>
	public int DefaultCitationSpanLength { get; set; } = 200;
}
