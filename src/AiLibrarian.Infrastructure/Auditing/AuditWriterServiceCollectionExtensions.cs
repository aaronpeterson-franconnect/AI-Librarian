using AiLibrarian.Auditing;
using AiLibrarian.Infrastructure.Persistence;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiLibrarian.Infrastructure.Auditing;

/// <summary>
/// DI registration for the audit writer. Closes ADR 0010 by giving the
/// host a real <see cref="IAuditWriter"/> instead of the
/// <c>NoOpAuditWriter</c> fallback registered by the LLM gateway.
/// </summary>
public static class AuditWriterServiceCollectionExtensions
{
	/// <summary>
	/// Register the Postgres-backed audit writer (with circuit breaker
	/// and startup probe) when <c>ConnectionStrings:Postgres</c> is set
	/// and <c>Auditing:WriterMode</c> is <see cref="AuditWriterMode.Postgres"/>.
	/// Otherwise leave the host's existing <see cref="IAuditWriter"/>
	/// registration alone (the LLM gateway's
	/// <c>TryAddSingleton&lt;NoOpAuditWriter&gt;</c> stays in effect).
	///
	/// <para>
	/// Must be called <b>after</b> <c>AddAiLibrarianLlmGateway</c> so
	/// this registration overrides the gateway's NoOp default.
	/// </para>
	/// </summary>
	public static IServiceCollection AddAiLibrarianAuditing(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		services
			.AddOptions<AuditingOptions>()
			.Bind(configuration.GetSection(AuditingOptions.SectionName));

		var section = configuration.GetSection(AuditingOptions.SectionName);
		var modeRaw = section["WriterMode"];
		var mode = string.IsNullOrWhiteSpace(modeRaw)
			? AuditWriterMode.Postgres
			: Enum.Parse<AuditWriterMode>(modeRaw, ignoreCase: true);

		if (mode != AuditWriterMode.Postgres)
		{
			return services;
		}

		if (!services.TryAddPostgresDataSource(configuration))
		{
			// No connection string — the host can run in dev-without-Postgres
			// mode with the NoOp writer; the startup probe will warn loudly.
			return services;
		}

		services.AddSingleton<PostgresAuditWriter>();
		services.AddSingleton<IAuditQueryService>(sp => sp.GetRequiredService<PostgresAuditWriter>());
		services.AddSingleton<AuditWriterCircuitBreaker>(sp =>
			new AuditWriterCircuitBreaker(
				sp.GetRequiredService<PostgresAuditWriter>(),
				sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditingOptions>>(),
				sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuditWriterCircuitBreaker>>()));
		services.Replace(ServiceDescriptor.Singleton<IAuditWriter>(sp => sp.GetRequiredService<AuditWriterCircuitBreaker>()));
		services.AddSingleton<IAuditWriterStatus>(sp => sp.GetRequiredService<AuditWriterCircuitBreaker>());

		services.AddHostedService<AuditWriterStartupProbe>();

		return services;
	}
}
