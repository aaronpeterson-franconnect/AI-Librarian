using AiLibrarian.Auditing;
using AiLibrarian.Infrastructure.Auditing;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace AiLibrarian.Infrastructure.Tests.Auditing;

/// <summary>
/// Unit tests for <see cref="AuditWriterStartupProbe"/>. Pin the
/// refuse-to-start contract from ADR 0010 + the degraded-mode escape
/// hatch.
/// </summary>
public sealed class AuditWriterStartupProbeTests
{
	private static AuditWriterStartupProbe BuildProbe(
		IAuditWriter writer,
		AuditingOptions options,
		IAuditQueryService? query = null)
		=> new(
			writer,
			Options.Create(options),
			NullLogger<AuditWriterStartupProbe>.Instance,
			query);

	[Fact]
	public async Task Healthy_writer_emits_ready_event()
	{
		var writer = Substitute.For<IAuditWriter>();
		var query = Substitute.For<IAuditQueryService>();
		query.IsLedgerReachableAsync(Arg.Any<CancellationToken>()).Returns(true);

		var probe = BuildProbe(writer, new AuditingOptions(), query);
		await probe.StartingAsync(CancellationToken.None);

		await writer.Received(1).WriteAsync(
			Arg.Is<AuditEvent>(e => e.EventSubtype == "system.audit.writer.ready" && e.Outcome == EventOutcome.Success),
			Arg.Any<AuditCriticality>(),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Unreachable_ledger_with_refuse_to_start_throws()
	{
		var writer = Substitute.For<IAuditWriter>();
		var query = Substitute.For<IAuditQueryService>();
		query.IsLedgerReachableAsync(Arg.Any<CancellationToken>()).Returns(false);

		var probe = BuildProbe(writer, new AuditingOptions
		{
			RefuseToStartOnProbeFailure = true,
			DegradedModeAllowed = false,
		}, query);

		await FluentActions.Invoking(() => probe.StartingAsync(CancellationToken.None))
			.Should().ThrowAsync<AuditWriterUnavailableException>();

		await writer.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Unreachable_ledger_with_degraded_mode_allows_start()
	{
		var writer = Substitute.For<IAuditWriter>();
		var query = Substitute.For<IAuditQueryService>();
		query.IsLedgerReachableAsync(Arg.Any<CancellationToken>()).Returns(false);

		var probe = BuildProbe(writer, new AuditingOptions
		{
			RefuseToStartOnProbeFailure = true,
			DegradedModeAllowed = true,
		}, query);

		await FluentActions.Invoking(() => probe.StartingAsync(CancellationToken.None))
			.Should().NotThrowAsync();
	}

	[Fact]
	public async Task Refuse_to_start_disabled_logs_and_continues()
	{
		var writer = Substitute.For<IAuditWriter>();
		var query = Substitute.For<IAuditQueryService>();
		query.IsLedgerReachableAsync(Arg.Any<CancellationToken>()).Returns(false);

		var probe = BuildProbe(writer, new AuditingOptions
		{
			RefuseToStartOnProbeFailure = false,
		}, query);

		await FluentActions.Invoking(() => probe.StartingAsync(CancellationToken.None))
			.Should().NotThrowAsync();
	}

	[Fact]
	public async Task Write_failure_during_probe_is_treated_as_probe_failure()
	{
		var writer = Substitute.For<IAuditWriter>();
		writer.WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>())
			.Returns(_ => Task.FromException(new AuditWriterUnavailableException("ledger gone")));

		var query = Substitute.For<IAuditQueryService>();
		query.IsLedgerReachableAsync(Arg.Any<CancellationToken>()).Returns(true);

		var probe = BuildProbe(writer, new AuditingOptions
		{
			RefuseToStartOnProbeFailure = true,
			DegradedModeAllowed = false,
		}, query);

		await FluentActions.Invoking(() => probe.StartingAsync(CancellationToken.None))
			.Should().ThrowAsync<AuditWriterUnavailableException>();
	}

	[Fact]
	public async Task Probe_skips_reachability_when_no_query_service()
	{
		// In hosts without a IAuditQueryService registration (NoOp mode),
		// the probe should still emit the ready event by attempting a
		// write — that's the only reachable signal.
		var writer = Substitute.For<IAuditWriter>();
		var probe = BuildProbe(writer, new AuditingOptions(), query: null);

		await probe.StartingAsync(CancellationToken.None);

		await writer.Received(1).WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<AuditCriticality>(), Arg.Any<CancellationToken>());
	}
}
