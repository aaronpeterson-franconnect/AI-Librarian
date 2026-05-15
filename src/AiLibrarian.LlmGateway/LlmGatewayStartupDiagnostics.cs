using System.Text.Json;

using AiLibrarian.Auditing;
using AiLibrarian.LlmGateway.Abstractions;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.LlmGateway;

/// <summary>
/// Emits ADR 0012 startup audit events for configured LLM providers and logs
/// warnings when tier metadata is <see cref="ProviderTier.Unverified"/> or when
/// enterprise tiers are missing a <see cref="DataHandlingProfile"/>.
/// </summary>
public sealed class LlmGatewayStartupDiagnostics : IHostedService
{
	private readonly IOptions<LlmGatewayOptions> _options;
	private readonly IAuditWriter _auditWriter;
	private readonly ILogger<LlmGatewayStartupDiagnostics> _logger;

	/// <summary>Creates <see cref="LlmGatewayStartupDiagnostics"/>.</summary>
	public LlmGatewayStartupDiagnostics(
		IOptions<LlmGatewayOptions> options,
		IAuditWriter auditWriter,
		ILogger<LlmGatewayStartupDiagnostics> logger)
	{
		_options = options;
		_auditWriter = auditWriter;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task StartAsync(CancellationToken cancellationToken)
	{
		var options = _options.Value;
		var descriptors = ProviderRegistry.Build(options);
		var correlationId = Guid.NewGuid();

		var configuredPayload = descriptors.Select(d => new
		{
			d.Id,
			d.DisplayName,
			tier = d.Tier.ToString(),
			d.Enabled,
			models = d.Models,
			dataHandling = d.DataHandling is null
				? null
				: new
				{
					d.DataHandling.NoTraining,
					d.DataHandling.RetentionDays,
					d.DataHandling.NoHumanReview,
					d.DataHandling.TenantBound,
					d.DataHandling.ContractReference,
				},
		}).ToArray();

		var configuredJson = JsonSerializer.Serialize(configuredPayload);

		await _auditWriter.WriteAsync(
			new AuditEvent(
				Id: Guid.NewGuid(),
				OccurredAt: DateTimeOffset.UtcNow,
				ActorUserId: AuditConstants.SystemUserId,
				ActorRole: null,
				OriginatedBy: null,
				DepartmentId: null,
				EventType: "audit",
				EventSubtype: "startup.providers_configured",
				TargetKind: "llm_gateway",
				TargetId: null,
				CorrelationId: correlationId,
				Outcome: EventOutcome.Success,
				ErrorClass: null,
				Llm: null,
				Details: new Dictionary<string, object?>
				{
					["providers_json"] = configuredJson,
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		foreach (var (id, provider) in options.Providers)
		{
			if (!provider.Enabled)
			{
				continue;
			}

			var unverifiedTier = provider.Tier == ProviderTier.Unverified;
			var missingDataHandling = provider.Tier != ProviderTier.Unverified && provider.DataHandling is null;

			if (!unverifiedTier && !missingDataHandling)
			{
				continue;
			}

			_logger.LogWarning(
				"LLM provider '{ProviderId}' ({DisplayName}) is enabled but tier metadata is incomplete. " +
				"Tier={Tier}. Update appsettings and docs/llm-providers.md per ADR 0012.",
				id,
				provider.DisplayName,
				provider.Tier);

			await _auditWriter.WriteAsync(
				new AuditEvent(
					Id: Guid.NewGuid(),
					OccurredAt: DateTimeOffset.UtcNow,
					ActorUserId: AuditConstants.SystemUserId,
					ActorRole: null,
					OriginatedBy: null,
					DepartmentId: null,
					EventType: "audit",
					EventSubtype: "startup.provider_tier_unverified",
					TargetKind: "llm_provider",
					TargetId: null,
					CorrelationId: correlationId,
					Outcome: EventOutcome.Partial,
					ErrorClass: unverifiedTier ? "tier_unverified" : "data_handling_missing",
					Llm: null,
					Details: new Dictionary<string, object?>
					{
						["provider_id"] = id,
						["display_name"] = provider.DisplayName,
						["tier"] = provider.Tier.ToString(),
						["reason"] = unverifiedTier ? "Tier is Unverified" : "DataHandling missing for non-Unverified tier",
					}),
				AuditCriticality.Critical,
				cancellationToken).ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
