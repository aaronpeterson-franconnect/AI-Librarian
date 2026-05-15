using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Testcontainer smoke for the bulk chunk-content reader. Verifies the
/// happy path (one chunk → full content), the per-chunk cap (returns
/// truncated content), and the missing-chunk omission (no exception,
/// just absent from the dictionary).
/// </summary>
public sealed class PostgresChunkContentReaderTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresChunkContentReaderTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task ReadContentAsync_Returns_Full_Markdown()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		var (chunkId, fullText) = await SeedChunkAsync(content: "alpha-beta-gamma-delta");

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var reader = new PostgresChunkContentReader(dataSource, NullLogger<PostgresChunkContentReader>.Instance);

		var result = await reader.ReadContentAsync(new[] { chunkId });

		result.Should().ContainKey(chunkId);
		result[chunkId].Should().Be(fullText);
	}

	[SkippableFact]
	public async Task ReadContentAsync_Truncates_To_Cap()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		// Seed a chunk with content longer than the cap.
		var content = new string('x', 5000);
		var (chunkId, _) = await SeedChunkAsync(content);

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var reader = new PostgresChunkContentReader(dataSource, NullLogger<PostgresChunkContentReader>.Instance);

		var result = await reader.ReadContentAsync(new[] { chunkId }, maxCharsPerChunk: 1000);

		result[chunkId].Length.Should().Be(1000, "server-side left() truncates to the cap");
	}

	[SkippableFact]
	public async Task ReadContentAsync_Omits_Unknown_Chunks()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var reader = new PostgresChunkContentReader(dataSource, NullLogger<PostgresChunkContentReader>.Instance);

		var unknown = Guid.NewGuid();
		var result = await reader.ReadContentAsync(new[] { unknown });

		result.Should().NotContainKey(unknown);
	}

	[SkippableFact]
	public async Task ReadContentAsync_Empty_Input_Returns_Empty()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

		await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
		var reader = new PostgresChunkContentReader(dataSource, NullLogger<PostgresChunkContentReader>.Instance);

		var result = await reader.ReadContentAsync(Array.Empty<Guid>());

		result.Should().BeEmpty();
	}

	private async Task<(Guid ChunkId, string FullText)> SeedChunkAsync(string content)
	{
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);
		var sourceId = await RlsTestData.InsertSourceAsync(
			_fixture.ConnectionString,
			departmentId: RlsTestData.EngineeringDeptId,
			classification: "Internal",
			contributorId: RlsTestData.EngineeringContributorId,
			contributorDepts: new[] { RlsTestData.EngineeringDeptId });

		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand("""
			INSERT INTO source_chunks (source_id, order_index, content_markdown, span_anchor)
			VALUES (@s, 0, @content, '{"type":"test"}'::jsonb)
			RETURNING id
			""", conn);
		cmd.Parameters.AddWithValue("s", sourceId);
		cmd.Parameters.AddWithValue("content", content);
		var chunkId = (Guid)(await cmd.ExecuteScalarAsync())!;
		return (chunkId, content);
	}
}
