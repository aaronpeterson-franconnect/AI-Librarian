using AiLibrarian.Auditing;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Infrastructure.Auditing;

/// <summary>
/// Startup-time health probe for the audit ledger. Mirrors the
/// <c>LlmGatewayStartupDiagnostics</c> pattern (ADR 0012) so the same
/// "configured / verified / failed" event shape lives in
/// <c>audit_events</c> for both the LLM gateway and the audit writer
/// itself.
///
/// <para>
/// Implements <see cref="IHostedLifecycleService"/> so the probe runs
/// in <c>StartingAsync</c> — <b>before</b> any
/// <c>IHostedService.StartAsync</c> (including
/// <c>LlmGatewayStartupDiagnostics</c>). Without this, a failed audit
/// ledger would surface as a confusing LLM-gateway error because the
/// gateway's startup audit-write is the first thing to touch Postgres.
/// </para>
///
/// <para>
/// On a healthy ledger, emits a <c>system.audit.writer.ready</c> audit
/// row. On failure, the behavior depends on
/// <see cref="AuditingOptions"/>:
/// <list type="bullet">
///   <item><description><see cref="AuditingOptions.RefuseToStartOnProbeFailure"/>
///   = <see langword="true"/> + <see cref="AuditingOptions.DegradedModeAllowed"/>
///   = <see langword="false"/> → throws so the host fails fast.</description></item>
///   <item><description>Degraded mode allowed → logs a warning and lets
///   the host start; the breaker will keep dropping events until the
///   ledger comes back.</description></item>
///   <item><description><see cref="AuditingOptions.RefuseToStartOnProbeFailure"/>
///   = <see langword="false"/> → unit-test mode; logs and continues.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class AuditWriterStartupProbe : IHostedLifecycleService
{
	private readonly IAuditWriter _writer;
	private readonly IAuditQueryService? _query;
	private readonly IOptions<AuditingOptions> _options;
	private readonly ILogger<AuditWriterStartupProbe> _logger;

	/// <summary>Creates the probe.</summary>
	public AuditWriterStartupProbe(
		IAuditWriter writer,
		IOptions<AuditingOptions> options,
		ILogger<AuditWriterStartupProbe> logger,
		IAuditQueryService? query = null)
	{
		_writer = writer;
		_query = query;
		_options = options;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task StartingAsync(CancellationToken cancellationToken)
	{
		var opts = _options.Value;
		var correlationId = Guid.NewGuid();

		var ledgerReachable = _query is null || await _query.IsLedgerReachableAsync(cancellationToken).ConfigureAwait(false);

		if (!ledgerReachable)
		{
			HandleProbeFailure(opts, reason: "ledger_unreachable");
			return;
		}

		try
		{
			await _writer.WriteAsync(
				new AuditEvent(
					Id: Guid.NewGuid(),
					OccurredAt: DateTimeOffset.UtcNow,
					ActorUserId: AuditConstants.SystemUserId,
					ActorRole: null,
					OriginatedBy: null,
					DepartmentId: null,
					EventType: "audit",
					EventSubtype: "system.audit.writer.ready",
					TargetKind: "audit_writer",
					TargetId: null,
					CorrelationId: correlationId,
					Outcome: EventOutcome.Success,
					ErrorClass: null,
					Llm: null,
					Details: new Dictionary<string, object?>
					{
						["writer_mode"] = opts.WriterMode.ToString(),
						["degraded_mode_allowed"] = opts.DegradedModeAllowed,
					}),
				AuditCriticality.Critical,
				cancellationToken).ConfigureAwait(false);

			_logger.LogInformation(
				"Audit writer ready (mode={WriterMode}, degraded_allowed={Degraded}).",
				opts.WriterMode, opts.DegradedModeAllowed);
		}
		catch (Exception ex)
		{
			HandleProbeFailure(opts, reason: "write_failed", inner: ex);
		}
	}

	/// <inheritdoc />
	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <inheritdoc />
	public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <inheritdoc />
	public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <inheritdoc />
	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <inheritdoc />
	public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	private void HandleProbeFailure(AuditingOptions opts, string reason, Exception? inner = null)
	{
		_logger.LogError(
			inner,
			"Audit writer startup probe failed (reason={Reason}, refuse_to_start={Refuse}, degraded_allowed={Degraded}).",
			reason, opts.RefuseToStartOnProbeFailure, opts.DegradedModeAllowed);

		if (opts.RefuseToStartOnProbeFailure && !opts.DegradedModeAllowed)
		{
			throw new AuditWriterUnavailableException(
				$"Audit writer startup probe failed (reason={reason}); refusing to start. Configure ConnectionStrings:Postgres or set Auditing:RefuseToStartOnProbeFailure=false for tests.",
				inner ?? new InvalidOperationException(reason));
		}
	}
}
