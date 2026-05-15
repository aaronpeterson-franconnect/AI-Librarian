using AiLibrarian.Domain.Citations;
using AiLibrarian.Quality;
using AiLibrarian.Quality.Tests.Fixtures;

namespace AiLibrarian.Quality.Tests;

/// <summary>
/// Acceptance from the hardening plan: "returns correct rows when source
/// chunks are soft-deleted in test fixtures." Plus the missing-chunk
/// case (hard-delete cascade or never-existed citation).
/// </summary>
public sealed class DanglingCitationDetectorTests
{
	[Fact]
	public async Task Reports_Missing_Chunk_When_Lookup_Returns_Nothing()
	{
		var citation = SyntheticClaims.ValidCitation(chunkId: Guid.NewGuid());
		var detector = new DanglingCitationDetector(new InMemoryChunkLookup());

		var result = await detector.FindAsync(new[] { citation });

		result.Should().ContainSingle()
			.Which.Reason.Should().Be(DanglingReason.ChunkMissing);
	}

	[Fact]
	public async Task Reports_SoftDeleted_Chunk()
	{
		var chunkId = Guid.NewGuid();
		var lookup = new InMemoryChunkLookup().Add(SyntheticClaims.SoftDeletedChunk(chunkId));
		var citation = SyntheticClaims.ValidCitation(chunkId);
		var detector = new DanglingCitationDetector(lookup);

		var result = await detector.FindAsync(new[] { citation });

		result.Should().ContainSingle()
			.Which.Reason.Should().Be(DanglingReason.ChunkSoftDeleted);
	}

	[Fact]
	public async Task Healthy_Citation_Is_Not_Dangling()
	{
		var chunkId = Guid.NewGuid();
		var lookup = new InMemoryChunkLookup().Add(SyntheticClaims.ValidChunk(chunkId));
		var citation = SyntheticClaims.ValidCitation(chunkId);
		var detector = new DanglingCitationDetector(lookup);

		var result = await detector.FindAsync(new[] { citation });

		result.Should().BeEmpty();
	}

	[Fact]
	public async Task Empty_Input_Returns_Empty_Result_Without_Calling_Lookup()
	{
		var detector = new DanglingCitationDetector(new InMemoryChunkLookup());
		var result = await detector.FindAsync(Array.Empty<Citation>());
		result.Should().BeEmpty();
	}
}
