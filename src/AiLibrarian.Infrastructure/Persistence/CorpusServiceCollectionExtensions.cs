using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Sources;
using AiLibrarian.Domain.Users;
using AiLibrarian.Domain.Wiki;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// DI registration for the read-side corpus repositories.
/// Pattern matches <c>AddPostgresHybridSearch</c>: when
/// <c>ConnectionStrings:Postgres</c> is set, register the Postgres
/// implementations; otherwise fall back to the null adapters so the
/// API can still boot in dev-without-Postgres mode.
/// </summary>
public static class CorpusServiceCollectionExtensions
{
	/// <summary>
	/// Register <see cref="ISourceRepository"/> and
	/// <see cref="IDepartmentRepository"/>.
	/// </summary>
	public static IServiceCollection AddPostgresCorpusRepositories(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		if (!services.TryAddPostgresDataSource(configuration))
		{
			services.AddSingleton<ISourceRepository, NullSourceRepository>();
			services.AddSingleton<IDepartmentRepository, NullDepartmentRepository>();
			services.AddSingleton<ISourceWriter, NullSourceWriter>();
			services.AddSingleton<ISourceTypeBackfiller, NullSourceTypeBackfiller>();
			services.AddSingleton<IUserDirectory, NullUserDirectory>();
			services.AddSingleton<IUserAuthorizationWriter, NullUserAuthorizationWriter>();
			// Dev-without-Postgres: in-memory grade sink + null chunk
			// lookup. The validator's rule-2 still fires correctly when
			// chunks don't resolve.
			services.AddSingleton<IClaimGradeSink, NullClaimGradeSink>();
			services.AddSingleton<IChunkLookup, NullChunkLookup>();
			services.AddSingleton<IChunkContentReader, NullChunkContentReader>();
			services.AddSingleton<IChunkSampler, NullChunkSampler>();
			services.AddSingleton<IWikiRevisionWriter, NullWikiRevisionWriter>();
			services.AddSingleton<IWikiProposalWriter, NullWikiProposalWriter>();
			services.AddSingleton<IWikiProposalReader, NullWikiProposalReader>();
			services.AddSingleton<IWikiPageReader, NullWikiPageReader>();
			services.AddSingleton<IWikiPageWriter, NullWikiPageWriter>();
			return services;
		}

		services.AddSingleton<ISourceRepository, PostgresSourceRepository>();
		services.AddSingleton<IDepartmentRepository, PostgresDepartmentRepository>();
		services.AddSingleton<ISourceWriter, PostgresSourceWriter>();
		services.AddSingleton<ISourceTypeBackfiller, PostgresSourceTypeBackfiller>();
		// PostgresUserDirectory is scoped so per-request cache doesn't bleed
		// across users -- two concurrent requests for different users would
		// otherwise share the projection cache.
		services.AddScoped<IUserDirectory, PostgresUserDirectory>();
		// Authorization writes always run in admin context; safe as singleton.
		services.AddSingleton<IUserAuthorizationWriter, PostgresUserAuthorizationWriter>();
		// Wiki-claim-grade persistence (Phase 2) + the Postgres-backed
		// IChunkLookup. Both run in system admin context internally,
		// safe as singletons.
		services.AddSingleton<IClaimGradeSink, PostgresClaimGradeSink>();
		services.AddSingleton<IChunkLookup, PostgresChunkLookup>();
		services.AddSingleton<IChunkContentReader, PostgresChunkContentReader>();
		services.AddSingleton<IChunkSampler, PostgresChunkSampler>();
		services.AddSingleton<IWikiRevisionWriter, PostgresWikiRevisionWriter>();
		services.AddSingleton<IWikiProposalWriter, PostgresWikiProposalWriter>();
		services.AddSingleton<IWikiProposalReader, PostgresWikiProposalReader>();
		services.AddSingleton<IWikiPageReader, PostgresWikiPageReader>();
		services.AddSingleton<IWikiPageWriter, PostgresWikiPageWriter>();
		return services;
	}
}
