using AiLibrarian.Auditing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Infrastructure.Auditing;

/// <summary>
/// Decorator over <see cref="IAuditWriter"/> that opens a circuit on
/// repeated failures, preventing a Postgres hiccup from cascading into
/// a self-DoS as every user-facing write retries the audit insert.
///
/// <para>
/// Default behavior is fail-closed: when the inner writer throws or
/// the breaker is open and <see cref="AuditingOptions.DegradedModeAllowed"/>
/// is <see langword="false"/>, the exception propagates so the calling
/// transaction rolls back. When degraded mode is allowed, the breaker
/// drops events with a warning log instead of throwing — used only in
/// emergency operator-flip scenarios per ADR 0010.
/// </para>
/// </summary>
public sealed class AuditWriterCircuitBreaker : IAuditWriter, IAuditWriterStatus
{
	private readonly IAuditWriter _inner;
	private readonly IOptions<AuditingOptions> _options;
	private readonly ILogger<AuditWriterCircuitBreaker> _logger;
	private readonly TimeProvider _time;

	private readonly Lock _gate = new();
	private CircuitState _state = CircuitState.Closed;
	private int _failureCount;
	private DateTimeOffset _openedAt;

	/// <summary>Creates the breaker.</summary>
	public AuditWriterCircuitBreaker(
		IAuditWriter inner,
		IOptions<AuditingOptions> options,
		ILogger<AuditWriterCircuitBreaker> logger)
		: this(inner, options, logger, TimeProvider.System)
	{
	}

	/// <summary>Creates the breaker with an explicit time provider (test seam).</summary>
	public AuditWriterCircuitBreaker(
		IAuditWriter inner,
		IOptions<AuditingOptions> options,
		ILogger<AuditWriterCircuitBreaker> logger,
		TimeProvider time)
	{
		_inner = inner;
		_options = options;
		_logger = logger;
		_time = time;
	}

	/// <summary>Current breaker state — exposed for tests and operator dashboards.</summary>
	public CircuitState State
	{
		get { lock (_gate) { return _state; } }
	}

	/// <inheritdoc />
	public AuditWriterStatusSnapshot GetStatus()
	{
		var opts = _options.Value;
		return new AuditWriterStatusSnapshot(
			Mode: "Postgres",
			DegradedModeAllowed: opts.DegradedModeAllowed,
			CircuitState: State.ToString());
	}

	/// <inheritdoc />
	public async Task WriteAsync(
		AuditEvent evt,
		AuditCriticality criticality = AuditCriticality.Critical,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(evt);

		var opts = _options.Value;
		var degradedAllowed = opts.DegradedModeAllowed;
		var bestEffort = criticality == AuditCriticality.BestEffort;

		// Read current state, possibly transitioning Open → HalfOpen.
		var snapshot = SnapshotState();

		if (snapshot == CircuitState.Open)
		{
			if (bestEffort || degradedAllowed)
			{
				LogDroppedEvent(evt, criticality, reason: bestEffort
					? "circuit_open_best_effort"
					: "circuit_open_degraded_allowed");
				return;
			}

			throw new AuditWriterUnavailableException(
				$"Audit writer circuit is open; refusing to take action without an audit row. Caller transaction must roll back. event_type={evt.EventType}/{evt.EventSubtype}.");
		}

		try
		{
			await _inner.WriteAsync(evt, criticality, cancellationToken).ConfigureAwait(false);
			OnSuccess();
		}
		catch (Exception ex) when (ex is not AuditWriterUnavailableException && ex is not OperationCanceledException)
		{
			// Best-effort: log and swallow; never trip the breaker, never throw.
			// Read-side noise must not penalize the write-side fail-closed contract.
			if (bestEffort)
			{
				_logger.LogWarning(
					ex,
					"Best-effort audit write failed event_type={EventType}/{EventSubtype} corr={CorrelationId} — caller proceeds.",
					evt.EventType, evt.EventSubtype, evt.CorrelationId);
				return;
			}

			var openedNow = OnFailure(opts.CircuitBreaker.FailureThreshold);
			_logger.LogError(
				ex,
				"Critical audit write failed event_type={EventType}/{EventSubtype} (failures={Failures}, opened_now={Opened})",
				evt.EventType, evt.EventSubtype, _failureCount, openedNow);

			if (degradedAllowed && openedNow)
			{
				LogDroppedEvent(evt, criticality, reason: "circuit_just_opened_degraded_allowed");
				return;
			}

			if (degradedAllowed && _state == CircuitState.Open)
			{
				LogDroppedEvent(evt, criticality, reason: "circuit_open_degraded_allowed_after_failure");
				return;
			}

			throw new AuditWriterUnavailableException(
				$"Audit write failed and degraded mode is not allowed; calling transaction must roll back. event_type={evt.EventType}/{evt.EventSubtype}.",
				ex);
		}
	}

	private CircuitState SnapshotState()
	{
		lock (_gate)
		{
			if (_state == CircuitState.Open)
			{
				var elapsed = _time.GetUtcNow() - _openedAt;
				if (elapsed >= _options.Value.CircuitBreaker.BreakDuration)
				{
					_state = CircuitState.HalfOpen;
					_logger.LogInformation("Audit writer circuit transitioned Open -> HalfOpen after {Elapsed}.", elapsed);
				}
			}

			return _state;
		}
	}

	private void OnSuccess()
	{
		lock (_gate)
		{
			if (_state != CircuitState.Closed)
			{
				_logger.LogInformation("Audit writer circuit transitioned {From} -> Closed.", _state);
			}

			_state = CircuitState.Closed;
			_failureCount = 0;
		}
	}

	private bool OnFailure(int threshold)
	{
		lock (_gate)
		{
			_failureCount++;
			if (_state != CircuitState.Open && _failureCount >= threshold)
			{
				_state = CircuitState.Open;
				_openedAt = _time.GetUtcNow();
				return true;
			}

			if (_state == CircuitState.HalfOpen)
			{
				_state = CircuitState.Open;
				_openedAt = _time.GetUtcNow();
				return true;
			}

			return false;
		}
	}

	private void LogDroppedEvent(AuditEvent evt, AuditCriticality criticality, string reason)
	{
		_logger.LogWarning(
			"Audit event dropped reason={Reason} crit={Criticality} id={Id} type={EventType}/{EventSubtype} actor={ActorUserId} corr={CorrelationId}",
			reason, criticality, evt.Id, evt.EventType, evt.EventSubtype, evt.ActorUserId, evt.CorrelationId);
	}
}

/// <summary>Circuit-breaker state.</summary>
public enum CircuitState
{
	/// <summary>Normal operation; calls flow through.</summary>
	Closed = 0,

	/// <summary>Failures exceeded threshold; calls fail fast.</summary>
	Open = 1,

	/// <summary>Probing; the next call is allowed through to test recovery.</summary>
	HalfOpen = 2,
}

/// <summary>
/// Thrown when an audit write cannot complete and degraded mode is not
/// allowed. The caller's transaction must roll back so the user-visible
/// action does not commit without its audit row.
/// </summary>
public sealed class AuditWriterUnavailableException : Exception
{
	/// <summary>Creates the exception.</summary>
	public AuditWriterUnavailableException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with an inner cause.</summary>
	public AuditWriterUnavailableException(string message, Exception inner)
		: base(message, inner)
	{
	}
}
