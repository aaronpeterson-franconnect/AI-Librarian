using AiLibrarian.Auditing;
using AiLibrarian.Infrastructure.Auditing;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace AiLibrarian.Infrastructure.Tests.Auditing;

/// <summary>
/// Unit tests for the audit-writer circuit breaker. Pin the
/// fail-closed contract (ADR 0010) and the degraded-mode escape hatch
/// against a substitute inner writer; integration tests against a real
/// Postgres land in the Phase 1 RLS chaos battery.
/// </summary>
public sealed class AuditWriterCircuitBreakerTests
{
	private static AuditEvent SampleEvent(Guid? id = null)
		=> new(
			Id: id ?? Guid.NewGuid(),
			OccurredAt: DateTimeOffset.UtcNow,
			ActorUserId: AuditConstants.SystemUserId,
			ActorRole: null,
			OriginatedBy: null,
			DepartmentId: null,
			EventType: "test",
			EventSubtype: "circuit_breaker",
			TargetKind: null,
			TargetId: null,
			CorrelationId: Guid.NewGuid(),
			Outcome: EventOutcome.Success,
			ErrorClass: null,
			Llm: null,
			Details: new Dictionary<string, object?>());

	private static AuditWriterCircuitBreaker BuildBreaker(
		IAuditWriter inner,
		AuditingOptions options,
		FakeTimeProvider? time = null)
		=> new(
			inner,
			Options.Create(options),
			NullLogger<AuditWriterCircuitBreaker>.Instance,
			time ?? new FakeTimeProvider());

	[Fact]
	public async Task Closed_state_passes_calls_through()
	{
		var inner = Substitute.For<IAuditWriter>();
		var breaker = BuildBreaker(inner, new AuditingOptions());

		await breaker.WriteAsync(SampleEvent());

		await inner.Received(1).WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>());
		breaker.State.Should().Be(CircuitState.Closed);
	}

	[Fact]
	public async Task Failures_below_threshold_keep_breaker_closed_and_propagate()
	{
		var inner = Substitute.For<IAuditWriter>();
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ => Task.FromException(new InvalidOperationException("boom")));

		var breaker = BuildBreaker(inner, new AuditingOptions
		{
			CircuitBreaker = new CircuitBreakerOptions { FailureThreshold = 3 },
		});

		for (var i = 0; i < 2; i++)
		{
			await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
				.Should().ThrowAsync<AuditWriterUnavailableException>();
		}

		breaker.State.Should().Be(CircuitState.Closed);
	}

	[Fact]
	public async Task Failures_at_threshold_open_breaker_and_short_circuit()
	{
		var inner = Substitute.For<IAuditWriter>();
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ => Task.FromException(new InvalidOperationException("boom")));

		var breaker = BuildBreaker(inner, new AuditingOptions
		{
			CircuitBreaker = new CircuitBreakerOptions { FailureThreshold = 2 },
		});

		// Two failures hit the threshold and open the circuit.
		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
			.Should().ThrowAsync<AuditWriterUnavailableException>();
		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
			.Should().ThrowAsync<AuditWriterUnavailableException>();

		breaker.State.Should().Be(CircuitState.Open);

		// Subsequent calls short-circuit without touching the inner writer.
		inner.ClearReceivedCalls();
		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
			.Should().ThrowAsync<AuditWriterUnavailableException>();
		await inner.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Open_with_degraded_mode_drops_event_silently()
	{
		var inner = Substitute.For<IAuditWriter>();
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ => Task.FromException(new InvalidOperationException("boom")));

		var breaker = BuildBreaker(inner, new AuditingOptions
		{
			DegradedModeAllowed = true,
			CircuitBreaker = new CircuitBreakerOptions { FailureThreshold = 1 },
		});

		// First failure both opens the breaker AND completes the call (degraded).
		await breaker.WriteAsync(SampleEvent());
		breaker.State.Should().Be(CircuitState.Open);

		// Subsequent calls in Open + degraded short-circuit cleanly.
		inner.ClearReceivedCalls();
		await breaker.WriteAsync(SampleEvent());
		await inner.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Half_open_recovers_to_closed_on_success()
	{
		var time = new FakeTimeProvider();
		var inner = Substitute.For<IAuditWriter>();

		var calls = 0;
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ =>
			{
				calls++;
				return calls <= 2
					? Task.FromException(new InvalidOperationException("boom"))
					: Task.CompletedTask;
			});

		var breaker = BuildBreaker(inner, new AuditingOptions
		{
			CircuitBreaker = new CircuitBreakerOptions
			{
				FailureThreshold = 2,
				BreakDuration = TimeSpan.FromSeconds(30),
			},
		}, time);

		// Trip the breaker.
		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
			.Should().ThrowAsync<AuditWriterUnavailableException>();
		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
			.Should().ThrowAsync<AuditWriterUnavailableException>();
		breaker.State.Should().Be(CircuitState.Open);

		// Advance past break duration → next call probes (HalfOpen) and
		// the inner writer succeeds, closing the breaker.
		time.Advance(TimeSpan.FromSeconds(31));
		await breaker.WriteAsync(SampleEvent());
		breaker.State.Should().Be(CircuitState.Closed);
	}

	[Fact]
	public async Task Half_open_failure_reopens_breaker()
	{
		var time = new FakeTimeProvider();
		var inner = Substitute.For<IAuditWriter>();
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ => Task.FromException(new InvalidOperationException("still broken")));

		var breaker = BuildBreaker(inner, new AuditingOptions
		{
			CircuitBreaker = new CircuitBreakerOptions
			{
				FailureThreshold = 1,
				BreakDuration = TimeSpan.FromSeconds(10),
			},
		}, time);

		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
			.Should().ThrowAsync<AuditWriterUnavailableException>();
		breaker.State.Should().Be(CircuitState.Open);

		time.Advance(TimeSpan.FromSeconds(11));
		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
			.Should().ThrowAsync<AuditWriterUnavailableException>();

		breaker.State.Should().Be(CircuitState.Open);
	}

	[Fact]
	public async Task Cancellation_propagates_without_tripping_breaker()
	{
		var inner = Substitute.For<IAuditWriter>();
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ => Task.FromException(new OperationCanceledException()));

		var breaker = BuildBreaker(inner, new AuditingOptions
		{
			CircuitBreaker = new CircuitBreakerOptions { FailureThreshold = 1 },
		});

		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent()))
			.Should().ThrowAsync<OperationCanceledException>();

		// OperationCanceledException must not count as a circuit failure;
		// caller cancellations during shutdown are not audit-ledger faults.
		breaker.State.Should().Be(CircuitState.Closed);
	}

	[Fact]
	public async Task Null_event_throws_argument_null()
	{
		var inner = Substitute.For<IAuditWriter>();
		var breaker = BuildBreaker(inner, new AuditingOptions());

		await FluentActions.Invoking(() => breaker.WriteAsync(null!))
			.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task BestEffort_failure_does_not_throw_or_trip_breaker()
	{
		var inner = Substitute.For<IAuditWriter>();
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ => Task.FromException(new InvalidOperationException("boom")));

		var breaker = BuildBreaker(inner, new AuditingOptions
		{
			CircuitBreaker = new CircuitBreakerOptions { FailureThreshold = 1 },
		});

		// 5 best-effort failures must not throw and must not open the breaker.
		for (var i = 0; i < 5; i++)
		{
			await FluentActions
				.Invoking(() => breaker.WriteAsync(SampleEvent(), AuditCriticality.BestEffort))
				.Should().NotThrowAsync();
		}

		breaker.State.Should().Be(CircuitState.Closed);
	}

	[Fact]
	public async Task BestEffort_short_circuits_when_breaker_open_without_throwing()
	{
		var inner = Substitute.For<IAuditWriter>();
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ => Task.FromException(new InvalidOperationException("boom")));

		var breaker = BuildBreaker(inner, new AuditingOptions
		{
			CircuitBreaker = new CircuitBreakerOptions { FailureThreshold = 1 },
		});

		// Trip the breaker with a Critical write.
		await FluentActions.Invoking(() => breaker.WriteAsync(SampleEvent(), AuditCriticality.Critical))
			.Should().ThrowAsync<AuditWriterUnavailableException>();
		breaker.State.Should().Be(CircuitState.Open);

		// BestEffort writes while Open are dropped silently — never reach the inner.
		inner.ClearReceivedCalls();
		await breaker.WriteAsync(SampleEvent(), AuditCriticality.BestEffort);
		await inner.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task BestEffort_passes_criticality_to_inner_writer()
	{
		var captured = AuditCriticality.Critical;
		var inner = Substitute.For<IAuditWriter>();
		inner.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				captured = call.Arg<AuditCriticality>();
				return Task.CompletedTask;
			});

		var breaker = BuildBreaker(inner, new AuditingOptions());
		await breaker.WriteAsync(SampleEvent(), AuditCriticality.BestEffort);

		captured.Should().Be(AuditCriticality.BestEffort);
	}

	private sealed class FakeTimeProvider : TimeProvider
	{
		private DateTimeOffset _now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan delta) => _now = _now.Add(delta);
	}
}
