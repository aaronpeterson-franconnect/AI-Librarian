using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Pgvector.Npgsql;

namespace AiLibrarian.Infrastructure.Persistence;

/// <summary>
/// Shared registration for the singleton <see cref="NpgsqlDataSource"/>
/// every Postgres-touching component (retrieval, audit writer, future
/// wiki/page reads) shares. Idempotent — first caller wins.
/// </summary>
public static class PostgresDataSourceServiceCollectionExtensions
{
	/// <summary>
	/// Register a singleton <see cref="NpgsqlDataSource"/> bound to
	/// <c>ConnectionStrings:Postgres</c>, with the <c>vector</c>
	/// extension type-mapping enabled. Returns <see langword="true"/>
	/// when a connection string is configured (registration ran);
	/// <see langword="false"/> when the caller should fall back to a
	/// null/no-op adapter.
	/// </summary>
	public static bool TryAddPostgresDataSource(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		var cs = configuration.GetConnectionString("Postgres");
		if (string.IsNullOrWhiteSpace(cs))
		{
			return false;
		}

		services.TryAddSingleton<NpgsqlDataSource>(_ =>
		{
			var builder = new NpgsqlDataSourceBuilder(cs);
			builder.UseVector();
			return builder.Build();
		});

		return true;
	}
}
