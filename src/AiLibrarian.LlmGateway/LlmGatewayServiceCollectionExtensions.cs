using AiLibrarian.Auditing;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.LlmGateway.Internal;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// DI registration for the LLM gateway (Semantic Kernel + Azure OpenAI).
/// </summary>
public static class LlmGatewayServiceCollectionExtensions
{
	/// <summary>
	/// Registers kernel, chat/embedding/rerank providers, ADR 0012 startup
	/// diagnostics, and a default <see cref="IAuditWriter"/> when none is
	/// supplied by the host.
	/// </summary>
	public static IServiceCollection AddAiLibrarianLlmGateway(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		services.AddLogging();

		services
			.AddOptions<LlmGatewayOptions>()
			.Bind(configuration.GetSection(LlmGatewayOptions.SectionName));

		services.TryAddSingleton<IAuditWriter, NoOpAuditWriter>();

		services.AddSingleton(sp =>
		{
			var options = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value;
			var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
			return LlmKernelFactory.BuildKernel(options, loggerFactory);
		});

		services.AddSingleton<IChatProvider>(sp =>
		{
			var options = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value;
			if (IsAzureOpenAiDefault(options)
				&& LlmKernelFactory.TryGetEnabledAzureOpenAi(options, out _))
			{
				return new AzureOpenAiChatProvider(
					sp.GetRequiredService<Kernel>(),
					sp.GetRequiredService<IAuditWriter>(),
					sp.GetRequiredService<ILogger<AzureOpenAiChatProvider>>());
			}

			return new UnconfiguredChatProvider(options.DefaultChatProvider);
		});

		services.AddSingleton<IEmbeddingProvider>(sp =>
		{
			var options = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value;
			if (IsAzureOpenAiEmbeddingDefault(options)
				&& LlmKernelFactory.TryGetEnabledAzureOpenAi(options, out _))
			{
				return new AzureOpenAiEmbeddingProvider(
					sp.GetRequiredService<Kernel>(),
					sp.GetRequiredService<IAuditWriter>(),
					sp.GetRequiredService<ILogger<AzureOpenAiEmbeddingProvider>>());
			}

			return new UnconfiguredEmbeddingProvider(options.DefaultEmbeddingProvider);
		});

		services.AddSingleton<IRerankProvider, NoOpRerankProvider>();

		services.AddHostedService<LlmGatewayStartupDiagnostics>();

		return services;
	}

	private static bool IsAzureOpenAiDefault(LlmGatewayOptions options)
		=> string.Equals(options.DefaultChatProvider, "azure-openai", StringComparison.OrdinalIgnoreCase);

	private static bool IsAzureOpenAiEmbeddingDefault(LlmGatewayOptions options)
		=> string.Equals(options.DefaultEmbeddingProvider, "azure-openai", StringComparison.OrdinalIgnoreCase);
}
