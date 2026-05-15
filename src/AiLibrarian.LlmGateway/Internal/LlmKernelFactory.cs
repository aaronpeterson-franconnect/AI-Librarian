using AiLibrarian.LlmGateway;

using Azure.Core;
using Azure.Identity;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiLibrarian.LlmGateway.Internal;

/// <summary>
/// Builds a <see cref="Kernel"/> with Azure OpenAI chat + embedding services
/// when the <c>azure-openai</c> provider is enabled and sufficiently configured.
/// </summary>
internal static class LlmKernelFactory
{
	internal static bool TryGetEnabledAzureOpenAi(LlmGatewayOptions options, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProviderOptions? azure)
	{
		azure = null;
		if (!options.Providers.TryGetValue("azure-openai", out var p) || !p.Enabled)
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(p.Endpoint)
			|| string.IsNullOrWhiteSpace(p.ChatDeployment)
			|| string.IsNullOrWhiteSpace(p.EmbeddingDeployment))
		{
			return false;
		}

		azure = p;
		return true;
	}

	internal static Kernel BuildKernel(LlmGatewayOptions options, ILoggerFactory loggerFactory)
	{
		var kb = Kernel.CreateBuilder();
		kb.Services.AddSingleton(loggerFactory);

		if (!TryGetEnabledAzureOpenAi(options, out var azure))
		{
			return kb.Build();
		}

		var endpoint = NormalizeEndpoint(azure!.Endpoint!);

		if (!string.IsNullOrWhiteSpace(azure.ApiKey))
		{
			kb.AddAzureOpenAIChatCompletion(
				deploymentName: azure.ChatDeployment!,
				endpoint: endpoint,
				apiKey: azure.ApiKey,
				serviceId: "azure-openai-chat",
				modelId: null);

			kb.AddAzureOpenAITextEmbeddingGeneration(
				deploymentName: azure.EmbeddingDeployment!,
				endpoint: endpoint,
				apiKey: azure.ApiKey,
				serviceId: "azure-openai-embedding",
				modelId: null);
		}
		else
		{
			TokenCredential credential = new DefaultAzureCredential();
			kb.AddAzureOpenAIChatCompletion(
				deploymentName: azure.ChatDeployment!,
				endpoint: endpoint,
				credentials: credential,
				serviceId: "azure-openai-chat",
				modelId: null);

			kb.AddAzureOpenAITextEmbeddingGeneration(
				deploymentName: azure.EmbeddingDeployment!,
				endpoint: endpoint,
				credential: credential,
				serviceId: "azure-openai-embedding",
				modelId: null);
		}

		return kb.Build();
	}

	private static string NormalizeEndpoint(string endpoint)
		=> endpoint.Trim().TrimEnd('/');
}
