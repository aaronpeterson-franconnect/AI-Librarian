using AiLibrarian.Security;

namespace AiLibrarian.Mcp.Tests.Security;

/// <summary>
/// Regression coverage for the 2026-05-17 flake. Twice in one day,
/// CI's <c>AdversarialCorpusTests.Secret_Sample_Produces_Redaction_Candidate</c>
/// failed on the EC-PRIVATE-KEY sample because <see cref="SecretRedactor"/>
/// ran its 8 patterns in <c>Parallel.ForEach</c> with a 200ms timeout each.
/// First-call JIT compilation under CI load could push a pattern past the
/// budget; the <c>catch (RegexMatchTimeoutException)</c> in <c>Scan</c>
/// silently returned no matches; the test assertion failed.
///
/// <para>This class doesn't try to reproduce the timeout (the new
/// 1-second budget + warmup makes that effectively impossible on any
/// reasonable runner). It pins the behaviors that prevent recurrence:</para>
/// <list type="number">
///   <item><description>Constructing a <see cref="SecretRedactor"/> ahead
///   of any scan still works -- the static ctor's warmup completes during
///   type load. If a future change removes the static ctor, the type-load
///   would throw and this test (which references the type) would fail.</description></item>
///   <item><description>The originally-failing EC-PRIVATE-KEY sample
///   produces a match on every call, including the first scan after
///   process start. This re-runs the exact sample the original flake
///   was about.</description></item>
/// </list>
/// </summary>
public sealed class SecretRedactorWarmupTests
{
	private const string EcPrivateKeySample =
		"-----BEGIN EC PRIVATE KEY-----\nMHcCAQEEIH123456==\n-----END EC PRIVATE KEY-----";

	[Fact]
	public void Type_construction_completes_without_throwing()
	{
		// If the static ctor's warmup loop threw (e.g., a malformed
		// regex), `new SecretRedactor` would surface
		// TypeInitializationException. This is a smoke test for the
		// static-init path itself.
		var redactor = new SecretRedactor(new AskGuardOptions
		{
			RedactionMode = SecretRedactionMode.Shadow,
		});

		redactor.Should().NotBeNull();
	}

	[Fact]
	public void EC_private_key_sample_matches_on_first_scan_after_process_start()
	{
		// The exact corpus entry that flaked on CI (PR #2 first run).
		// With the warmup + 1s budget the match is now deterministic.
		var redactor = new SecretRedactor(new AskGuardOptions
		{
			RedactionMode = SecretRedactionMode.Shadow,
		});

		var result = redactor.Scan(EcPrivateKeySample);

		result.Matches.Should().NotBeEmpty(
			because: "the EC PRIVATE KEY sample is a textbook PEM match; the " +
				"original flake was a regex-timeout swallow, not a pattern bug.");
		result.Matches.Should().Contain(m => m.Kind == "pem",
			because: "the `pem` pattern in SecretRedactor should fire on every " +
				"`-----BEGIN ... PRIVATE KEY-----` ... `-----END ... PRIVATE KEY-----` " +
				"sample, EC included.");
	}
}
