using AiLibrarian.Domain;
using AiLibrarian.Domain.Personas;
using AiLibrarian.Infrastructure.Retrieval;

namespace AiLibrarian.Infrastructure.Tests.Retrieval;

/// <summary>
/// Unit coverage for the persona reranker. The reranker only reorders
/// an already-RLS-filtered list, so these tests focus on the multiplier
/// arithmetic, stability under neutral profiles, and the visibility
/// invariant ("a hit in the output must have been in the input").
/// </summary>
public sealed class PersonaRerankerTests
{
	private static readonly Guid HomeDept = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
	private static readonly Guid OtherDept = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
	private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Neutral_profile_preserves_input_order()
	{
		// Three hits in descending score order. Neutral profile → no change.
		var hits = new[]
		{
			Hit(score: 0.9, dept: HomeDept),
			Hit(score: 0.7, dept: OtherDept),
			Hit(score: 0.5, dept: HomeDept),
		};

		var result = PersonaReranker.Rerank(
			hits,
			PersonaRetrievalProfile.Neutral,
			callerDepartmentIds: new[] { HomeDept },
			now: Now,
			limit: 3);

		result.Should().HaveCount(3);
		result.Select(h => h.HybridScore).Should().Equal(0.9, 0.7, 0.5);
	}

	[Fact]
	public void Cross_department_boost_lifts_same_dept_hit_above_other_dept_hit()
	{
		// Two hits with the same hybrid score, one same-dept, one other-dept.
		// SameDept boost > 1.0 should put the same-dept hit first; tie-break
		// would otherwise leave original order.
		var sameDept = Hit(score: 0.5, dept: HomeDept, chunkId: Guid.NewGuid());
		var otherDept = Hit(score: 0.5, dept: OtherDept, chunkId: Guid.NewGuid());

		// Provide them in OPPOSITE order from desired -- proves the rerank
		// did the work rather than the input order leaking through.
		var hits = new[] { otherDept, sameDept };

		var profile = ProfileWithCrossDept(sameDept: 1.5, crossInternal: 1.0, crossShared: 1.0);

		var result = PersonaReranker.Rerank(hits, profile, new[] { HomeDept }, Now, limit: 2);

		result.Should().HaveCount(2);
		result[0].SourceDepartmentId.Should().Be(HomeDept);
		result[1].SourceDepartmentId.Should().Be(OtherDept);
	}

	[Fact]
	public void Cross_shared_boost_applied_to_confidential_other_dept_hits()
	{
		// Other-dept Confidential hit (caller can see it via a share) should
		// get crossShared, not crossInternal.
		var confidential = Hit(
			score: 0.5,
			dept: OtherDept,
			classification: Classification.Confidential,
			chunkId: Guid.NewGuid());
		var internalOther = Hit(
			score: 0.5,
			dept: OtherDept,
			classification: Classification.Internal,
			chunkId: Guid.NewGuid());

		var profile = ProfileWithCrossDept(sameDept: 1.0, crossInternal: 1.0, crossShared: 2.0);

		var result = PersonaReranker.Rerank(new[] { internalOther, confidential }, profile, new[] { HomeDept }, Now, limit: 2);

		// Confidential (other dept) gets shared boost → ranks first.
		result[0].SourceClassification.Should().Be(Classification.Confidential);
	}

	[Fact]
	public void Recency_decay_lifts_newer_chunks()
	{
		var fresh = Hit(score: 0.5, dept: HomeDept, createdAt: Now.AddDays(-1));
		var ancient = Hit(score: 0.5, dept: HomeDept, createdAt: Now.AddDays(-365));

		var profile = ProfileWithRecency(halfLifeDays: 90);

		var result = PersonaReranker.Rerank(new[] { ancient, fresh }, profile, new[] { HomeDept }, Now, limit: 2);

		result[0].SourceCreatedAt.Should().Be(Now.AddDays(-1));
	}

	[Fact]
	public void Authority_bias_applies_current_vs_draft()
	{
		var approved = Hit(score: 0.5, dept: HomeDept, approvedAt: Now.AddDays(-10));
		var draft = Hit(score: 0.5, dept: HomeDept, approvedAt: null);

		var profile = new PersonaRetrievalProfile(
			SourceTypeWeights: new Dictionary<string, double>(),
			RecencyHalfLifeDays: null,
			AuthorityBias: new Dictionary<string, double> { ["current"] = 1.5, ["draft"] = 0.5 },
			CrossDepartmentBoost: null,
			FloorClassification: null);

		var result = PersonaReranker.Rerank(new[] { draft, approved }, profile, new[] { HomeDept }, Now, limit: 2);

		result[0].SourceApprovedAt.Should().NotBeNull("approved sources get the 'current' multiplier");
	}

	[Fact]
	public void Visibility_invariant_no_new_hits_in_output()
	{
		// Whatever the reranker does, the output set must be a subset of the
		// input. This is the structural safety check from ADR 0015.
		var input = Enumerable.Range(0, 5)
			.Select(_ => Hit(score: 0.5, dept: HomeDept))
			.ToArray();

		var result = PersonaReranker.Rerank(
			input,
			ProfileWithCrossDept(2.0, 1.0, 1.0),
			new[] { HomeDept },
			Now,
			limit: 3);

		result.Should().HaveCount(3);
		result.Should().AllSatisfy(h => input.Should().Contain(h, "every output hit must come from the input"));
	}

	[Fact]
	public void Empty_input_returns_empty()
	{
		var result = PersonaReranker.Rerank(
			Array.Empty<HybridChunkHit>(),
			PersonaRetrievalProfile.Neutral,
			Array.Empty<Guid>(),
			Now,
			limit: 10);

		result.Should().BeEmpty();
	}

	[Fact]
	public void Limit_zero_returns_empty()
	{
		var hits = new[] { Hit(score: 0.5, dept: HomeDept) };
		var result = PersonaReranker.Rerank(hits, PersonaRetrievalProfile.Neutral, new[] { HomeDept }, Now, limit: 0);
		result.Should().BeEmpty();
	}

	[Fact]
	public void Limit_above_input_count_returns_all()
	{
		var hits = new[]
		{
			Hit(score: 0.9, dept: HomeDept),
			Hit(score: 0.7, dept: HomeDept),
		};

		var result = PersonaReranker.Rerank(hits, PersonaRetrievalProfile.Neutral, new[] { HomeDept }, Now, limit: 10);

		result.Should().HaveCount(2);
	}

	[Fact]
	public void ComputeMultiplier_is_one_when_profile_dimensions_disabled()
	{
		var hit = Hit(score: 0.5, dept: HomeDept);
		var m = PersonaReranker.ComputeMultiplier(hit, PersonaRetrievalProfile.Neutral, new[] { HomeDept }, Now);
		m.Should().Be(1.0);
	}

	[Fact]
	public void Source_type_weight_lifts_matching_hit_above_unmatched()
	{
		var codeHit = Hit(score: 0.5, dept: HomeDept, sourceType: "code", chunkId: Guid.NewGuid());
		var emailHit = Hit(score: 0.5, dept: HomeDept, sourceType: "email", chunkId: Guid.NewGuid());

		var profile = ProfileWithSourceTypeWeights(new Dictionary<string, double>
		{
			["code"] = 1.5,
			["email"] = 0.5,
		});

		var result = PersonaReranker.Rerank(new[] { emailHit, codeHit }, profile, new[] { HomeDept }, Now, limit: 2);

		result[0].SourceType.Should().Be("code");
		result[1].SourceType.Should().Be("email");
	}

	[Fact]
	public void Source_type_weight_skips_hits_with_null_source_type()
	{
		// One hit has source_type=code (gets boosted), the other has
		// SourceType=null (no weight applied; multiplier stays at 1.0).
		var codeHit = Hit(score: 0.5, dept: HomeDept, sourceType: "code");
		var unclassifiedHit = Hit(score: 0.5, dept: HomeDept, sourceType: null);

		var profile = ProfileWithSourceTypeWeights(new Dictionary<string, double>
		{
			["code"] = 2.0,
		});

		var result = PersonaReranker.Rerank(
			new[] { unclassifiedHit, codeHit }, profile, new[] { HomeDept }, Now, limit: 2);

		result[0].SourceType.Should().Be("code",
			"code hit gets the 2.0 weight; the unclassified hit stays at 1.0");
		result[1].SourceType.Should().BeNull();
	}

	[Fact]
	public void Source_type_with_no_matching_weight_is_treated_as_no_opinion()
	{
		// Hit has source_type=ticket, but the profile only weights code.
		// No weight applied -> multiplier stays at 1.0.
		var ticketHit = Hit(score: 0.5, dept: HomeDept, sourceType: "ticket");
		var profile = ProfileWithSourceTypeWeights(new Dictionary<string, double>
		{
			["code"] = 2.0,
		});

		var m = PersonaReranker.ComputeMultiplier(ticketHit, profile, new[] { HomeDept }, Now);
		m.Should().Be(1.0);
	}

	// --- helpers ---

	private static HybridChunkHit Hit(
		double score,
		Guid dept,
		Classification classification = Classification.Internal,
		Guid? chunkId = null,
		DateTimeOffset? createdAt = null,
		DateTimeOffset? approvedAt = null,
		string? sourceType = null)
		=> new(
			ChunkId: chunkId ?? Guid.NewGuid(),
			SourceId: Guid.NewGuid(),
			OrderIndex: 0,
			Excerpt: "excerpt",
			HybridScore: score,
			CosineDistance: 0.1,
			TextRank: 0.5,
			SourceClassification: classification,
			SourceDepartmentId: dept,
			SourceCreatedAt: createdAt,
			SourceApprovedAt: approvedAt,
			SourceType: sourceType);

	private static PersonaRetrievalProfile ProfileWithSourceTypeWeights(IReadOnlyDictionary<string, double> weights)
		=> new(
			SourceTypeWeights: weights,
			RecencyHalfLifeDays: null,
			AuthorityBias: new Dictionary<string, double>(),
			CrossDepartmentBoost: null,
			FloorClassification: null);

	private static PersonaRetrievalProfile ProfileWithCrossDept(double sameDept, double crossInternal, double crossShared)
		=> new(
			SourceTypeWeights: new Dictionary<string, double>(),
			RecencyHalfLifeDays: null,
			AuthorityBias: new Dictionary<string, double>(),
			CrossDepartmentBoost: new PersonaCrossDepartmentBoost(sameDept, crossInternal, crossShared),
			FloorClassification: null);

	private static PersonaRetrievalProfile ProfileWithRecency(int halfLifeDays)
		=> new(
			SourceTypeWeights: new Dictionary<string, double>(),
			RecencyHalfLifeDays: halfLifeDays,
			AuthorityBias: new Dictionary<string, double>(),
			CrossDepartmentBoost: null,
			FloorClassification: null);
}
