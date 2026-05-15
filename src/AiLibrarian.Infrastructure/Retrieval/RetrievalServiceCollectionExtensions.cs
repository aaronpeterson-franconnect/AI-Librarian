using AiLibrarian.Domain.Personas;
using AiLibrarian.Infrastructure.Personas;
using AiLibrarian.Infrastructure.Persistence;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiLibrarian.Infrastructure.Retrieval;

public static class RetrievalServiceCollectionExtensions
{
	/// <summary>
	/// Registers <see cref="IHybridChunkSearch"/> + the persona-aware
	/// rerank decorator (ADR 0015) when
	/// <c>ConnectionStrings:Postgres</c> is set; otherwise a no-op
	/// fallback for dev-without-Postgres mode.
	/// </summary>
	public static IServiceCollection AddPostgresHybridSearch(this IServiceCollection services, IConfiguration configuration)
	{
		// Bind options regardless of whether Postgres is configured -- the
		// decorator's options shape is part of the public configuration
		// surface even when the path is dormant.
		services.AddOptions<PersonaRetrievalOptions>()
			.Bind(configuration.GetSection(PersonaRetrievalOptions.SectionName));

		if (!services.TryAddPostgresDataSource(configuration))
		{
			services.AddSingleton<IHybridChunkSearch, NullHybridChunkSearch>();
			services.AddSingleton<IPersonaProfileReader, NullPersonaProfileReader>();
			return services;
		}

		// Concrete inner search; decorator wraps it for IHybridChunkSearch
		// resolutions. Callers that need the un-reranked inner (e.g. tests
		// pinning the raw hybrid score) can resolve HybridChunkSearch
		// directly. The decorator's `inner` parameter is typed as the
		// interface for testability, so the factory below explicitly hands
		// it the concrete to avoid a self-resolution cycle.
		services.AddSingleton<HybridChunkSearch>();
		services.AddSingleton<IPersonaProfileReader, PostgresPersonaProfileReader>();
		services.AddSingleton<IHybridChunkSearch>(sp =>
			new PersonaAwareHybridChunkSearch(
				inner: sp.GetRequiredService<HybridChunkSearch>(),
				profileReader: sp.GetRequiredService<IPersonaProfileReader>(),
				options: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PersonaRetrievalOptions>>(),
				logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PersonaAwareHybridChunkSearch>>()));
		return services;
	}
}
