using System.Runtime.CompilerServices;

using AiLibrarian.Security;

namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// End-to-end wiring proof: AskGuard + SecretRedactor wired into the
/// eval harness produce the metrics the hardening-plan CI gate needs.
/// Doesn't aim to verify AskGuard's own behavior (covered in
/// AiLibrarian.Mcp.Tests) — it verifies the bridge.
/// </summary>
public sealed class AdversarialMetricsTests
{
	private const string Caller = "00000000-0000-0000-0000-00000000eval";

	[Fact]
	public async Task Jailbreak_Run_Counts_Admitted_Vs_Refused()
	{
		var guard = MakeGuard(new AskGuardOptions
		{
			MaxQueryBytes = 100,
			RateLimitPerMinutePerCaller = 1000,
		});

		var inputs = new[]
		{
			"short jailbreak attempt",                        // admitted
			new string('x', 200),                              // refused (cap)
			"another short one",                               // admitted
		};

		var report = await AdversarialMetrics.RunJailbreaksAsync(guard, inputs, Caller);

		report.Total.Should().Be(3);
		report.AdmittedBySocketWithLlmDeciding.Should().Be(2);
		report.RefusedByGuard.Should().Be(1);
		report.AdmitRate.Should().BeApproximately(2.0 / 3.0, 0.001);
	}

	[Fact]
	public void Secret_Run_Counts_Samples_With_Candidates()
	{
		var redactor = new SecretRedactor(new AskGuardOptions
		{
			RedactionMode = SecretRedactionMode.Shadow,
		});

		var samples = new[]
		{
			"benign text, no secrets here",
			"key=AKIAIOSFODNN7EXAMPLE in code",
			"random other text",
			"-----BEGIN RSA PRIVATE KEY-----\nABCDEF\n-----END RSA PRIVATE KEY-----",
		};

		var report = AdversarialMetrics.RunSecretSamples(redactor, samples);

		report.Total.Should().Be(4);
		report.SamplesWithCandidates.Should().Be(2);
		report.DetectionRate.Should().BeApproximately(0.5, 0.001);
	}

	private static AskGuard MakeGuard(AskGuardOptions options) =>
		new(
			new SingleChunkRetrieval(),
			new EchoSynthesizer(),
			options: Microsoft.Extensions.Options.Options.Create(options));

	private sealed class SingleChunkRetrieval : IAskRetrieval
	{
		public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(AskGuardRequest request, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<RetrievedChunk>>(new[]
			{
				new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "Internal", "engineering", "stub"),
			});
	}

	private sealed class EchoSynthesizer : IAskSynthesizer
	{
		public Task<string> SynthesizeAsync(AskSynthesisRequest request, CancellationToken cancellationToken)
			=> Task.FromResult($"echo: {request.Query}");
	}

	// Convenience to keep async signatures honest even when not awaited.
	private static async ValueTask Yield([CallerMemberName] string? _ = null) => await Task.Yield();
}
