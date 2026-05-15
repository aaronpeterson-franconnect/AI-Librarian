using AiLibrarian.Domain;
using AiLibrarian.Domain.Personas;

namespace AiLibrarian.Infrastructure.Retrieval;

/// <summary>
/// Persona-aware reranker per ADR 0015. Operates over the RLS-filtered
/// hit list that <see cref="IHybridChunkSearch"/> returns and produces
/// a reordered list with persona profile weights multiplied into each
/// hit's <see cref="HybridChunkHit.HybridScore"/>.
///
/// <para><b>Visibility-safe by construction.</b> The reranker only
/// reorders an existing list — it never queries the database and
/// never widens the set of hits the caller may see. A hit absent from
/// the input is absent from the output; a hit present in the input
/// stays eligible after rerank.</para>
///
/// <para><b>Neutral stability.</b> When the profile is
/// <see cref="PersonaRetrievalProfile.Neutral"/> (or every applicable
/// weight is 1.0), the reranker returns hits in the same order as
/// the input. This means "persona = neutral" and "no persona" produce
/// the same ranking, which keeps regression tests deterministic.</para>
///
/// <para><b>v1 dimensions:</b> recency-decay, authority bias (derived
/// from <c>sources.approved_at</c>), and cross-department boost.
/// <c>sourceTypeWeights</c> is part of the profile JSON but ignored at
/// rerank time pending a source-type taxonomy column.</para>
/// </summary>
public static class PersonaReranker
{
	/// <summary>
	/// Apply <paramref name="profile"/> to <paramref name="hits"/>,
	/// stable-sort by the adjusted score, and return the top
	/// <paramref name="limit"/>.
	/// </summary>
	public static IReadOnlyList<HybridChunkHit> Rerank(
		IReadOnlyList<HybridChunkHit> hits,
		PersonaRetrievalProfile profile,
		IReadOnlyCollection<Guid> callerDepartmentIds,
		DateTimeOffset now,
		int limit)
	{
		ArgumentNullException.ThrowIfNull(hits);
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(callerDepartmentIds);

		if (hits.Count == 0 || limit <= 0)
		{
			return Array.Empty<HybridChunkHit>();
		}

		// Score every hit once. Build a parallel array of (adjusted, originalIndex)
		// so the sort below can be stable on ties (keeps the original
		// hybrid order between equally-weighted hits).
		var scored = new (double Score, int OriginalIndex, HybridChunkHit Hit)[hits.Count];
		for (var i = 0; i < hits.Count; i++)
		{
			var hit = hits[i];
			var multiplier = ComputeMultiplier(hit, profile, callerDepartmentIds, now);
			scored[i] = (hit.HybridScore * multiplier, i, hit);
		}

		// Sort: descending by adjusted score, ties broken by original index ascending.
		Array.Sort(scored, (a, b) =>
		{
			var byScore = b.Score.CompareTo(a.Score);
			return byScore != 0 ? byScore : a.OriginalIndex.CompareTo(b.OriginalIndex);
		});

		var take = Math.Min(limit, scored.Length);
		var result = new HybridChunkHit[take];
		for (var i = 0; i < take; i++)
		{
			result[i] = scored[i].Hit;
		}
		return result;
	}

	/// <summary>
	/// Multiplier this hit earns from the profile. Exposed for tests +
	/// debug introspection; the rerank itself multiplies internally.
	/// </summary>
	public static double ComputeMultiplier(
		HybridChunkHit hit,
		PersonaRetrievalProfile profile,
		IReadOnlyCollection<Guid> callerDepartmentIds,
		DateTimeOffset now)
	{
		var m = 1.0;

		// Source-type weight (ADR 0015 sourceTypeWeights). Requires both
		// the hit to carry a populated SourceType (migration 0028 +
		// ingestion classifier) AND the profile to declare a weight for
		// that type. Either-or-both missing leaves m at 1.0 (no opinion).
		if (profile.SourceTypeWeights.Count > 0
			&& hit.SourceType is { Length: > 0 } sourceType
			&& profile.SourceTypeWeights.TryGetValue(sourceType, out var typeWeight))
		{
			m *= typeWeight;
		}

		// Recency decay: exponential decay with the configured half-life.
		// Half-life=null disables this dimension entirely (m stays at 1.0).
		if (profile.RecencyHalfLifeDays is int halfLife and > 0 && hit.SourceCreatedAt is DateTimeOffset created)
		{
			var ageDays = Math.Max(0, (now - created).TotalDays);
			m *= Math.Pow(0.5, ageDays / halfLife);
		}

		// Authority bias: v1 derives "current" from approved_at IS NOT NULL,
		// "draft" otherwise. The profile may carry "canonical" and
		// "superseded" keys too -- we leave room for them but don't have
		// schema signals to populate them in v1.
		if (profile.AuthorityBias.Count > 0)
		{
			var key = hit.SourceApprovedAt is null ? "draft" : "current";
			if (profile.AuthorityBias.TryGetValue(key, out var authorityWeight))
			{
				m *= authorityWeight;
			}
		}

		// Cross-department boost: same-dept vs cross-dept (Internal-or-below
		// vs shared-Confidential+). For v1 we collapse "shared" into
		// "Confidential or higher" -- the source_shares table would be a
		// second query and isn't necessary for the boost to be useful.
		if (profile.CrossDepartmentBoost is PersonaCrossDepartmentBoost cdb)
		{
			var sameDept = hit.SourceDepartmentId != Guid.Empty
				&& callerDepartmentIds.Contains(hit.SourceDepartmentId);
			if (sameDept)
			{
				m *= cdb.SameDepartment;
			}
			else
			{
				m *= hit.SourceClassification >= Classification.Confidential
					? cdb.CrossDepartmentShared
					: cdb.CrossDepartmentInternal;
			}
		}

		return m;
	}
}
