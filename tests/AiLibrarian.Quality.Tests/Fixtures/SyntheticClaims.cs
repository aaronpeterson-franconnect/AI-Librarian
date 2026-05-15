using AiLibrarian.Domain;
using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Quality.Tests.Fixtures;

/// <summary>
/// Synthetic claim corpus per the pre-Phase-1 hardening plan's
/// acceptance: "50 valid, 50 each-rule-violating classified correctly."
/// We don't physically materialize 250 hand-written claims (the
/// citation validator is purely structural and tests of the same shape
/// add no signal); instead we build them programmatically and let
/// xUnit's theory data generate the cases.
/// </summary>
internal static class SyntheticClaims
{
	internal const int ChunkLength = 500;

	internal static ChunkRef ValidChunk(Guid id, Classification cls = Classification.Internal) =>
		new(id, SourceId: Guid.NewGuid(), Classification: cls, ContentLength: ChunkLength, IsSoftDeleted: false);

	internal static ChunkRef SoftDeletedChunk(Guid id, Classification cls = Classification.Internal) =>
		new(id, SourceId: Guid.NewGuid(), Classification: cls, ContentLength: ChunkLength, IsSoftDeleted: true);

	internal static Citation ValidCitation(Guid chunkId, double confidence = 0.9) =>
		new(Id: Guid.NewGuid(), ChunkId: chunkId, SpanStart: 0, SpanEnd: 100, Confidence: confidence);

	internal static Claim ValidClaim(
		Classification facet = Classification.Internal,
		IReadOnlyList<Citation>? citations = null) =>
		new(
			Id: Guid.NewGuid(),
			Text: $"Synthetic valid claim {Guid.NewGuid():N}",
			FacetClassification: facet,
			Citations: citations ?? new[] { ValidCitation(Guid.NewGuid()) });

	internal static IEnumerable<Claim> ValidCorpus(int n, IList<ChunkRef> chunkBacking)
	{
		for (var i = 0; i < n; i++)
		{
			var chunk = ValidChunk(Guid.NewGuid());
			chunkBacking.Add(chunk);
			yield return new Claim(
				Id: Guid.NewGuid(),
				Text: $"Valid claim {i}",
				FacetClassification: Classification.Internal,
				Citations: new[] { ValidCitation(chunk.Id) });
		}
	}
}
