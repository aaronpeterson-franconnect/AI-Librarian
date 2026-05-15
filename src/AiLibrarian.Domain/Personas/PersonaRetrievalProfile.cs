namespace AiLibrarian.Domain.Personas;

/// <summary>
/// Persona-aware retrieval profile per ADR 0015. Loaded from the
/// <c>personas.retrieval_profile</c> JSONB column; missing fields fall
/// back to a neutral value (multiplier 1.0 / no adjustment) so a
/// half-configured profile degrades to the same behavior as no profile
/// rather than to a broken one.
///
/// <para><b>Visibility-safe.</b> Per ADR 0015 §"Persona must not change
/// visibility": the profile only re-weights ranking; it never enters
/// the RLS predicate or the WHERE clause of the retrieval query.
/// Persona reranking always runs <em>after</em> RLS has narrowed the
/// candidate set.</para>
///
/// <para><b>Scope.</b> All four dimensions are wired:
/// <see cref="SourceTypeWeights"/> (matched against
/// <c>sources.source_type</c> per the
/// <see cref="AiLibrarian.Domain.Sources.SourceType"/> taxonomy from
/// migration 0028), <see cref="RecencyHalfLifeDays"/>,
/// <see cref="AuthorityBias"/>, and
/// <see cref="CrossDepartmentBoost"/>. Hits whose
/// <c>SourceType</c> is null (the column is nullable to allow
/// backfill of pre-classifier rows) get no source-type weight —
/// equivalent to "no opinion" on that dimension.
/// <see cref="FloorClassification"/> stays informational; the
/// structural floor lives on <c>personas.classification_floor</c>.</para>
/// </summary>
/// <param name="SourceTypeWeights">Map of <see cref="AiLibrarian.Domain.Sources.SourceType"/> string → multiplier. Hits with a populated source type get the matching weight applied; unmatched + null get 1.0.</param>
/// <param name="RecencyHalfLifeDays">Half-life in days for exponential recency decay applied to <c>sources.created_at</c>. Null means "no recency adjustment."</param>
/// <param name="AuthorityBias">Map of authority status → multiplier. v1 maps <c>sources.approved_at IS NOT NULL</c> → <c>"current"</c> and <c>IS NULL</c> → <c>"draft"</c>.</param>
/// <param name="CrossDepartmentBoost">Multipliers for same-department vs cross-department hits. Null means "no department-relationship adjustment."</param>
/// <param name="FloorClassification">Informational echo of <c>personas.classification_floor</c> from ADR 0015; not applied here.</param>
public sealed record PersonaRetrievalProfile(
	IReadOnlyDictionary<string, double> SourceTypeWeights,
	int? RecencyHalfLifeDays,
	IReadOnlyDictionary<string, double> AuthorityBias,
	PersonaCrossDepartmentBoost? CrossDepartmentBoost,
	Classification? FloorClassification)
{
	/// <summary>
	/// The neutral profile: empty maps, no recency adjustment, no
	/// cross-department boost. Yields a multiplier of exactly 1.0 for
	/// every hit, which guarantees rerank stability — neutral persona
	/// produces the same order as no persona at all.
	/// </summary>
	public static PersonaRetrievalProfile Neutral { get; } = new(
		SourceTypeWeights: new Dictionary<string, double>(StringComparer.Ordinal),
		RecencyHalfLifeDays: null,
		AuthorityBias: new Dictionary<string, double>(StringComparer.Ordinal),
		CrossDepartmentBoost: null,
		FloorClassification: null);
}

/// <summary>Cross-department weighting per ADR 0015.</summary>
/// <param name="SameDepartment">Multiplier applied when the hit's source department matches one of the caller's department lattice entries.</param>
/// <param name="CrossDepartmentInternal">Multiplier applied to other-department hits whose source is <see cref="Classification.Internal"/> or lower.</param>
/// <param name="CrossDepartmentShared">Multiplier applied to other-department hits the caller can read via a source share (<see cref="Classification.Confidential"/> / <see cref="Classification.Restricted"/>).</param>
public sealed record PersonaCrossDepartmentBoost(
	double SameDepartment,
	double CrossDepartmentInternal,
	double CrossDepartmentShared);
