namespace AiLibrarian.Quality;

/// <summary>
/// Knobs for the mechanical <see cref="CitationValidator"/>. Defaults
/// match the figures from ADR 0007; deployments tighten them per
/// classification or persona via the standard options pipeline.
/// </summary>
public sealed class CitationValidatorOptions
{
	/// <summary>Configuration section name (<c>Quality:CitationValidator</c>).</summary>
	public const string SectionName = "Quality:CitationValidator";

	/// <summary>
	/// Minimum acceptable citation confidence. Citations below this floor
	/// fail rule 5. Default 0.7 from ADR 0007.
	/// </summary>
	public double ConfidenceFloor { get; set; } = 0.7;
}
