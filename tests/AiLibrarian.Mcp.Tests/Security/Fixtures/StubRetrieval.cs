using AiLibrarian.Security;

namespace AiLibrarian.Mcp.Tests.Security.Fixtures;

/// <summary>Deterministic <see cref="IAskRetrieval"/> stub for AskGuard tests.</summary>
internal sealed class StubRetrieval : IAskRetrieval
{
	private readonly IReadOnlyList<RetrievedChunk> _chunks;

	public StubRetrieval(IReadOnlyList<RetrievedChunk> chunks)
	{
		_chunks = chunks;
	}

	public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(AskGuardRequest request, CancellationToken cancellationToken)
		=> Task.FromResult(_chunks);

	public static StubRetrieval Empty() => new(Array.Empty<RetrievedChunk>());

	public static StubRetrieval WithSimpleChunk(string text)
		=> new(new[]
		{
			new RetrievedChunk(
				ChunkId: Guid.NewGuid(),
				SourceId: Guid.NewGuid(),
				Classification: "Internal",
				Department: "engineering",
				Text: text),
		});
}
