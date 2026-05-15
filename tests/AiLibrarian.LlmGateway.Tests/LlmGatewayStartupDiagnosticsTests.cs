using System.Text;

using AiLibrarian.Auditing;
using AiLibrarian.LlmGateway;
using AiLibrarian.LlmGateway.Abstractions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NSubstitute;

namespace AiLibrarian.LlmGateway.Tests;

public sealed class LlmGatewayStartupDiagnosticsTests
{
	[Fact]
	public async Task StartAsync_emits_providers_configured_audit_always()
	{
		var audit = Substitute.For<IAuditWriter>();

		var json = /* lang=json */ """
			{
			  "LlmGateway": {
			    "DefaultChatProvider": "azure-openai",
			    "DefaultEmbeddingProvider": "azure-openai",
			    "Providers": {
			      "azure-openai": {
			        "DisplayName": "Azure OpenAI",
			        "Enabled": false,
			        "Tier": "Enterprise"
			      }
			    }
			  }
			}
			""";

		var configuration = new ConfigurationBuilder()
			.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
			.Build();

		var services = new ServiceCollection();
		services.AddSingleton(audit);
		services.AddAiLibrarianLlmGateway(configuration);

		await using var provider = services.BuildServiceProvider();

		var hosted = provider.GetServices<IHostedService>()
			.OfType<LlmGatewayStartupDiagnostics>()
			.Single();

		await hosted.StartAsync(CancellationToken.None);

		await audit.Received(1).WriteAsync(
			Arg.Is<AuditEvent>(e =>
				e.EventType == "audit"
				&& e.EventSubtype == "startup.providers_configured"
				&& e.Outcome == EventOutcome.Success),
			Arg.Any<AuditCriticality>(),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task StartAsync_emits_tier_unverified_when_enabled_provider_lacks_metadata()
	{
		var audit = Substitute.For<IAuditWriter>();

		var json = /* lang=json */ """
			{
			  "LlmGateway": {
			    "DefaultChatProvider": "azure-openai",
			    "DefaultEmbeddingProvider": "azure-openai",
			    "Providers": {
			      "azure-openai": {
			        "DisplayName": "Azure OpenAI",
			        "Enabled": true,
			        "Endpoint": "https://example.openai.azure.com",
			        "ChatDeployment": "gpt-4o",
			        "EmbeddingDeployment": "text-embedding-3-large",
			        "Tier": "Unverified"
			      }
			    }
			  }
			}
			""";

		var configuration = new ConfigurationBuilder()
			.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
			.Build();

		var services = new ServiceCollection();
		services.AddSingleton(audit);
		services.AddAiLibrarianLlmGateway(configuration);

		await using var provider = services.BuildServiceProvider();

		var hosted = provider.GetServices<IHostedService>()
			.OfType<LlmGatewayStartupDiagnostics>()
			.Single();

		await hosted.StartAsync(CancellationToken.None);

		await audit.Received(1).WriteAsync(
			Arg.Is<AuditEvent>(e =>
				e.EventSubtype == "startup.providers_configured"),
			Arg.Any<AuditCriticality>(),
			Arg.Any<CancellationToken>());

		await audit.Received(1).WriteAsync(
			Arg.Is<AuditEvent>(e =>
				e.EventSubtype == "startup.provider_tier_unverified"
				&& e.Outcome == EventOutcome.Partial),
			Arg.Any<AuditCriticality>(),
			Arg.Any<CancellationToken>());
	}
}
