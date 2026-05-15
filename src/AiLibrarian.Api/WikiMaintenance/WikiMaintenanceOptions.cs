namespace AiLibrarian.Api.WikiMaintenance;

/// <summary>
/// Knobs for the API-side wiki maintenance surfaces:
/// <list type="bullet">
///   <item>The on-demand <c>POST /api/admin/wiki/maintain</c> endpoint.</item>
///   <item>The <c>WikiMaintenanceHostedService</c> cascade-regeneration sweep.</item>
/// </list>
///
/// <para>Both share the source-retrieval defaults (how many chunks
/// pulled via <c>IHybridChunkSearch</c>, what hybrid weight). The
/// hosted service adds its own scheduling knobs.</para>
/// </summary>
public sealed class WikiMaintenanceOptions
{
	/// <summary>Configuration section name (<c>WikiMaintenance</c>).</summary>
	public const string SectionName = "WikiMaintenance";

	/// <summary>
	/// Master switch for the periodic <c>WikiMaintenanceHostedService</c>.
	/// When false (default), the hosted service starts but immediately
	/// returns -- the on-demand admin endpoint still works.
	/// </summary>
	public bool CascadeRegenerationEnabled { get; set; }

	/// <summary>Periodic schedule for the cascade-regeneration sweep. Default 1 hour.</summary>
	public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

	/// <summary>How many chunks <c>IHybridChunkSearch</c> returns per maintenance call. Default 20.</summary>
	public int RetrievalLimit { get; set; } = 20;

	/// <summary>Hybrid vector vs. text weight (0..1) passed to retrieval. Default 0.6.</summary>
	public double HybridVectorWeight { get; set; } = 0.6;

	/// <summary>
	/// Cap on how many facets the cascade-regeneration sweep regenerates
	/// per tick. Per-tick bounded work keeps the worker from monopolizing
	/// LLM tokens after a big soft-delete event. Default 10.
	/// </summary>
	public int MaxFacetsPerCascadeTick { get; set; } = 10;

	/// <summary>
	/// Cap on canonical-content length per chunk in the source pool
	/// that gets handed to Pass 1. Default 4096. The hybrid-search
	/// hit's <c>Excerpt</c> is truncated at 600; this builder upgrades
	/// to full text up to this cap. Bounded so one giant chunk
	/// (e.g. a multi-page PDF canonicalized as a single chunk) can't
	/// blow up the LLM context window. Set to 600 to disable the
	/// upgrade and use the retrieval excerpts as-is.
	/// </summary>
	public int MaxChunkContentChars { get; set; } = 4096;
}
