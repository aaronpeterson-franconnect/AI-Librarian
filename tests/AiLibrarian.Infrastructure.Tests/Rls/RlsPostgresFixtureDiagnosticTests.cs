namespace AiLibrarian.Infrastructure.Tests.Rls;

/// <summary>
/// Diagnostic-only: when the testcontainer fixture fails to initialize,
/// every dependent <c>[SkippableFact]</c> silently skips. That made CI
/// runs look green for months despite zero testcontainer work
/// happening. This test fails LOUDLY with the underlying exception
/// surfaced so CI logs explain what went wrong.
///
/// <para>Default behaviour on a workstation without Docker: this test
/// skips with a clear "install Docker or accept testcontainer-skipped
/// runs" message. To force the test to fail noisily even without
/// Docker (e.g. to validate the diagnostic itself), set the
/// <c>AILIB_RLS_FIXTURE_REQUIRED=1</c> env var.</para>
/// </summary>
public sealed class RlsPostgresFixtureDiagnosticTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public RlsPostgresFixtureDiagnosticTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact]
	public void Fixture_initialised_or_skipped_with_clear_reason()
	{
		if (_fixture.IsAvailable)
		{
			// Happy path: fixture booted, tests can talk to it. No-op.
			return;
		}

		// On CI we MUST have testcontainers working -- a silently-skipped
		// RLS battery defeats the hardening-plan gate. The opt-in env var
		// lets local dev keep running without Docker.
		var ciRequiresFixture =
			Environment.GetEnvironmentVariable("CI") == "true"
			|| Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
			|| Environment.GetEnvironmentVariable("AILIB_RLS_FIXTURE_REQUIRED") == "1";

		if (ciRequiresFixture)
		{
			// Force the failure to carry the actual reason so the CI log
			// shows WHY testcontainers couldn't start.
			throw new InvalidOperationException(
				"RLS Postgres testcontainer fixture failed to initialize on CI. "
				+ "This breaks the entire RLS battery and the audit-append-only "
				+ "tests. Underlying reason follows:\n\n"
				+ (_fixture.UnavailableReason ?? "(no reason captured)"));
		}

		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
	}
}
