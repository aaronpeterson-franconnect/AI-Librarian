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

		// Mock-mode escape hatch. The Semantic Kernel AzureOpenAI client
		// validates that the endpoint starts with `https://` (an
		// Azure.AI.OpenAI / Verify.StartsWith check we can't bypass).
		// For docker-compose stacks using the AiLibrarian.LlmMock service
		// (see docker-compose.llm-mock.yml), the endpoint is plain HTTP
		// on the compose network. In that case we route through the
		// OpenAI client instead -- which accepts an HttpClient with a
		// custom BaseAddress, skips the https assertion, and calls
		// `/v1/embeddings` instead of `/openai/deployments/X/embeddings`.
		// The mock registers both routes so either client shape works.
		// Production paths (https://*.openai.azure.com) continue using
		// the AzureOpenAI client as before.
		if (IsHttpMockEndpoint(endpoint))
		{
			AddOpenAiClientForMock(kb, endpoint, azure);
			return kb.Build();
		}

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

	private static bool IsHttpMockEndpoint(string endpoint)
		=> endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Register the OpenAI client (NOT AzureOpenAI) with a custom
	/// HttpClient pointing at the mock. Only used when the configured
	/// endpoint is plain HTTP (see <see cref="IsHttpMockEndpoint"/>).
	/// </summary>
	private static void AddOpenAiClientForMock(IKernelBuilder kb, string endpoint, ProviderOptions azure)
	{
		// HttpClient lifetime: Kernel disposes its services on Kernel
		// disposal; for our purposes the kernel is a singleton, so the
		// HttpClient lives for the process. The mock is in-network so
		// SocketsHttpHandler defaults are fine.
		var httpClient = new HttpClient { BaseAddress = new Uri(endpoint) };

		// Embedding ID becomes the model identifier on the OpenAI shape;
		// the mock ignores it but downstream code carries it through
		// telemetry, so we pass the configured EmbeddingDeployment name
		// so audit / activity tags stay consistent.
		kb.AddOpenAITextEmbeddingGeneration(
			modelId: azure.EmbeddingDeployment!,
			apiKey: string.IsNullOrWhiteSpace(azure.ApiKey) ? "mock-key" : azure.ApiKey!,
			httpClient: httpClient,
			serviceId: "azure-openai-embedding");

		// Chat: same routing. The mock doesn't implement /v1/chat/completions
		// yet, but the registration must succeed so the kernel is fully
		// wired. Calls to chat will fail at runtime against the mock --
		// that's acceptable since the mock-mode smoke only exercises
		// the embeddings path.
		kb.AddOpenAIChatCompletion(
			modelId: azure.ChatDeployment!,
			apiKey: string.IsNullOrWhiteSpace(azure.ApiKey) ? "mock-key" : azure.ApiKey!,
			httpClient: httpClient,
			serviceId: "azure-openai-chat");
	}

	private static string NormalizeEndpoint(string endpoint)
		=> endpoint.Trim().TrimEnd('/');
}
