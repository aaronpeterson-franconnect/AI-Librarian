using Microsoft.Extensions.Logging;

namespace AiLibrarian.Auditing;

/// <summary>
/// Logs audit events to <see cref="ILogger"/> instead of persisting
/// them. Use only for unit tests and local bootstrapping; production
/// requires the Postgres-backed implementation in
/// <c>AiLibrarian.Infrastructure</c>.
/// </summary>
public sealed class NoOpAuditWriter : IAuditWriter
{
	private readonly ILogger<NoOpAuditWriter> _logger;

	/// <summary>Initializes a new <see cref="NoOpAuditWriter"/>.</summary>
	public NoOpAuditWriter(ILogger<NoOpAuditWriter> logger)
	{
		_logger = logger;
	}

	/// <inheritdoc />
	public Task WriteAsync(
		AuditEvent evt,
		AuditCriticality criticality = AuditCriticality.Critical,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(evt);

		_logger.LogInformation(
			"audit (no-op): type={EventType}/{EventSubtype} actor={ActorUserId} target={TargetKind}/{TargetId} outcome={Outcome} crit={Criticality} corr={CorrelationId}",
			evt.EventType,
			evt.EventSubtype,
			evt.ActorUserId,
			evt.TargetKind,
			evt.TargetId,
			evt.Outcome,
			criticality,
			evt.CorrelationId);

		return Task.CompletedTask;
	}
}
