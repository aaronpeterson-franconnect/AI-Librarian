using AiLibrarian.LlmGateway;
using AiLibrarian.LlmGateway.Abstractions;

namespace AiLibrarian.LlmGateway.Tests;

public sealed class ProviderRegistryTests
{
	[Fact]
	public void Build_projects_each_configured_provider()
	{
		var options = new LlmGatewayOptions
		{
			DefaultChatProvider = "azure-openai",
			DefaultEmbeddingProvider = "azure-openai",
			Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
			{
				["azure-openai"] = new()
				{
					DisplayName = "Azure OpenAI",
					Enabled = true,
					Tier = ProviderTier.Enterprise,
					DataHandling = new DataHandlingProfileOptions
					{
						NoTraining = true,
						RetentionDays = 30,
						NoHumanReview = true,
						TenantBound = true,
						ContractReference = "Azure OpenAI EA",
					},
					Models = new[] { "gpt-4o" },
				},
			},
		};

		var descriptors = ProviderRegistry.Build(options);

		descriptors.Should().HaveCount(1);
		var d = descriptors[0];
		d.Id.Should().Be("azure-openai");
		d.Tier.Should().Be(ProviderTier.Enterprise);
		d.Enabled.Should().BeTrue();
		d.DataHandling.Should().NotBeNull();
		d.DataHandling!.NoTraining.Should().BeTrue();
		d.DataHandling.RetentionDays.Should().Be(30);
		d.Models.Should().ContainSingle().Which.Should().Be("gpt-4o");
	}

	[Fact]
	public void Build_yields_unverified_descriptor_when_tier_unset()
	{
		var options = new LlmGatewayOptions
		{
			Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
			{
				["mystery"] = new() { DisplayName = "Mystery", Enabled = false },
			},
		};

		var descriptors = ProviderRegistry.Build(options);

		descriptors.Should().HaveCount(1);
		descriptors[0].Tier.Should().Be(ProviderTier.Unverified);
		descriptors[0].DataHandling.Should().BeNull();
	}
}
