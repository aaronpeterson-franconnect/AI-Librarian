using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Quality.Tests.Fixtures;

/// <summary>
/// Dictionary-backed <see cref="IChunkLookup"/> for tests. Mirrors the
/// Postgres-backed implementation the Infrastructure project will ship
/// for Phase 2; same interface, no database.
/// </summary>
internal sealed class InMemoryChunkLookup : IChunkLookup
{
	private readonly Dictionary<Guid, ChunkRef> _byId = new();

	public InMemoryChunkLookup Add(ChunkRef chunk)
	{
		_byId[chunk.Id] = chunk;
		return this;
	}

	public Task<IReadOnlyDictionary<Guid, ChunkRef>> ResolveAsync(
		IReadOnlyCollection<Guid> chunkIds,
		CancellationToken cancellationToken = default)
	{
		var result = new Dictionary<Guid, ChunkRef>();
		foreach (var id in chunkIds)
		{
			if (_byId.TryGetValue(id, out var c))
			{
				result[id] = c;
			}
		}

		return Task.FromResult<IReadOnlyDictionary<Guid, ChunkRef>>(result);
	}
}
