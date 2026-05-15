using AiLibrarian.Auditing;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.WikiMaintainer;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.WikiMaintenance;

/// <summary>
/// Cascade-Regeneration Worker per ADR 0006 + 0007. Periodically calls
/// the <c>audit_dangling_citations</c> SQL function (migration 0026),
/// groups the result by facet, and re-runs the Wiki Maintainer for
/// each affected (page, classification, persona) cell.
///
/// <para>Off when <see cref="WikiMaintenanceOptions.CascadeRegenerationEnabled"/>
/// is false. Always bounded per tick by
/// <see cref="WikiMaintenanceOptions.MaxFacetsPerCascadeTick"/> so a
/// big soft-delete event doesn't drain the LLM budget all at once.</para>
///
/// <para>Each tick audits: per-facet outcomes (<c>wiki/regen.facet</c>)
/// + a tick summary (<c>wiki/regen.tick</c>). Critical criticality:
/// silent regeneration failure would defeat the cascade contract.</para>
/// </summary>
internal sealed class WikiMaintenanceHostedService : BackgroundService
{
	private readonly IServiceProvider _services;
	private readonly IOptions<WikiMaintenanceOptions> _options;
	private readonly ILogger<WikiMaintenanceHostedService> _logger;

	public WikiMaintenanceHostedService(
		IServiceProvider services,
		IOptions<WikiMaintenanceOptions> options,
		ILogger<WikiMaintenanceHostedService> logger)
	{
		_services = services;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var opts = _options.Value;
		if (!opts.CascadeRegenerationEnabled)
		{
			_logger.LogInformation("WikiMaintenanceHostedService: cascade-regeneration disabled (WikiMaintenance:CascadeRegenerationEnabled=false).");
			return;
		}

		var interval = opts.Interval < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : opts.Interval;
		_logger.LogInformation("WikiMaintenanceHostedService: starting; interval={Interval} maxFacetsPerTick={Max}", interval, opts.MaxFacetsPerCascadeTick);

		using var timer = new PeriodicTimer(interval);
		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
			{
				await RunOneTickAsync(stoppingToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// graceful shutdown
		}
	}

	/// <summary>
	/// Run one cascade-regeneration tick. Marked <see langword="internal"/>
	/// so the test project (which has
	/// <c>InternalsVisibleTo</c>) can drive a single tick deterministically
	/// without juggling the <c>PeriodicTimer</c>.
	/// </summary>
	internal async Task RunOneTickAsync(CancellationToken cancellationToken)
	{
		try
		{
			using var scope = _services.CreateScope();
			var reader = scope.ServiceProvider.GetRequiredService<IDanglingFacetReader>();
			var maintainer = scope.ServiceProvider.GetRequiredService<IWikiMaintainer>();
			var poolBuilder = scope.ServiceProvider.GetRequiredService<IWikiSourcePoolBuilder>();
			var numberer = scope.ServiceProvider.GetRequiredService<IWikiRevisionNumberer>();
			var proposalWriter = scope.ServiceProvider.GetRequiredService<IWikiProposalWriter>();
			var auditWriter = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
			var opts = _options.Value;

			// Expire stale proposals first -- cheap, no LLM involved.
			// 14-day SLA per ADR 0006 Q13: a pending proposal past its
			// expires_at transitions to state='expired' with reason
			// 'expired without review'.
			try
			{
				var expired = await proposalWriter.ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
				if (expired > 0)
				{
					_logger.LogInformation("Expired {Count} stale wiki proposal(s).", expired);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogError(ex, "Wiki proposal expiry sweep failed; next tick will retry.");
			}

			var dangling = await reader.FindAsync(
				since: null,
				departmentId: null,
				maxFacets: opts.MaxFacetsPerCascadeTick,
				cancellationToken).ConfigureAwait(false);

			if (dangling.Count == 0)
			{
				return;
			}

			var systemContext = RlsSessionContext.System();
			var regenerated = 0;
			var failed = 0;

			foreach (var facet in dangling)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					var pool = await poolBuilder
						.BuildAsync(systemContext, facet.PageTitle, cancellationToken)
						.ConfigureAwait(false);

					var revno = await numberer
						.NextAsync(facet.PageId, facet.Classification, facet.PersonaId, cancellationToken)
						.ConfigureAwait(false);

					var request = new WikiMaintenanceRequest(
						PageId: facet.PageId,
						FacetClassification: facet.Classification,
						PersonaId: facet.PersonaId,
						RevisionNumber: revno,
						Topic: facet.PageTitle,
						SourceChunks: pool.Chunks,
						AuthoredBy: AuditConstants.SystemUserId);

					var result = await maintainer.GenerateRevisionAsync(request, cancellationToken).ConfigureAwait(false);

					if (result.Succeeded)
					{
						regenerated++;
					}
					else
					{
						failed++;
					}

					await EmitFacetAuditAsync(auditWriter, facet, result, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					failed++;
					_logger.LogError(ex, "Cascade-regen failed for page={PageId} facet={Facet}", facet.PageId, facet.Classification);
				}
			}

			await EmitTickAuditAsync(auditWriter, dangling.Count, regenerated, failed, cancellationToken).ConfigureAwait(false);

			_logger.LogInformation(
				"Cascade-regen tick: dangling_facets={Dangling} regenerated={Regen} failed={Failed}",
				dangling.Count,
				regenerated,
				failed);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogError(ex, "Cascade-regen tick failed; next tick will retry.");
		}
	}

	private static Task EmitFacetAuditAsync(
		IAuditWriter auditWriter,
		DanglingFacet facet,
		WikiMaintenanceResult result,
		CancellationToken cancellationToken)
	{
		var details = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["page_id"] = facet.PageId.ToString("D"),
			["page_slug"] = facet.PageSlug,
			["page_title"] = facet.PageTitle,
			["department_id"] = facet.DepartmentId.ToString("D"),
			["classification"] = facet.Classification.ToString(),
			["persona_id"] = facet.PersonaId?.ToString("D"),
			["dangling_count"] = facet.DanglingCount,
			["claim_count"] = result.ClaimCount,
			["citation_count"] = result.CitationCount,
			["revision_id"] = result.RevisionId?.ToString("D"),
			["rejection_reason"] = result.RejectionReason,
		};

		var evt = new AuditEvent(
			Id: Guid.NewGuid(),
			OccurredAt: DateTimeOffset.UtcNow,
			ActorUserId: AuditConstants.SystemUserId,
			ActorRole: "system",
			OriginatedBy: null,
			DepartmentId: facet.DepartmentId,
			EventType: "wiki",
			EventSubtype: "regen.facet",
			TargetKind: "page_facet",
			TargetId: facet.PageId,
			CorrelationId: Guid.NewGuid(),
			Outcome: result.Succeeded ? EventOutcome.Success : EventOutcome.Failure,
			ErrorClass: result.Succeeded ? null : "WikiRegenRejected",
			Llm: null,
			Details: details);

		return auditWriter.WriteAsync(evt, AuditCriticality.Critical, cancellationToken);
	}

	private static Task EmitTickAuditAsync(
		IAuditWriter auditWriter,
		int processed,
		int regenerated,
		int failed,
		CancellationToken cancellationToken)
	{
		var details = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["facets_processed"] = processed,
			["facets_regenerated"] = regenerated,
			["facets_failed"] = failed,
		};

		var evt = new AuditEvent(
			Id: Guid.NewGuid(),
			OccurredAt: DateTimeOffset.UtcNow,
			ActorUserId: AuditConstants.SystemUserId,
			ActorRole: "system",
			OriginatedBy: null,
			DepartmentId: null,
			EventType: "wiki",
			EventSubtype: "regen.tick",
			TargetKind: "page_facet",
			TargetId: null,
			CorrelationId: Guid.NewGuid(),
			Outcome: failed == 0 ? EventOutcome.Success : EventOutcome.Partial,
			ErrorClass: failed > 0 ? "WikiRegenPartialFailure" : null,
			Llm: null,
			Details: details);

		return auditWriter.WriteAsync(evt, AuditCriticality.Critical, cancellationToken);
	}
}
