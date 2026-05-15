using AiLibrarian.Quality;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Eval.Calibration;

/// <summary>
/// Live calibration test. Loads the 20 calibration YAMLs, drives them
/// through <see cref="LlmClaimGrader"/> against a real Azure OpenAI
/// chat deployment, computes Cohen's κ between the human gold labels
/// and the judge's verdicts, and writes a JSON report
/// (<c>CalibrationReportWriter</c>) for the workflow gate to consume.
///
/// <para><b>Skipped by default.</b> The test fires only when both:
/// <list type="bullet">
///   <item><c>AILIB_LIVE_CALIBRATION=1</c> is set (operator opt-in
///         signal — keeps PR runs from accidentally billing tokens).</item>
///   <item><see cref="AzureOpenAiHttpChatProviderOptions.FromEnvironment"/>
///         finds <c>AZURE_OPENAI_ENDPOINT</c>,
///         <c>AZURE_OPENAI_API_KEY</c>, and
///         <c>AZURE_OPENAI_CHAT_DEPLOYMENT</c> in the env.</item>
/// </list>
/// </para>
///
/// <para>The test is deliberately tolerant: it does NOT fail on
/// κ &lt; 0.7. That threshold is the workflow's <em>warn</em> gate
/// (per the hardening plan) and is applied downstream by parsing
/// the JSON report. The test only fails on operator misconfiguration
/// (no calibration cases found) or hard κ collapse (κ &lt; 0.0 means
/// less-than-chance agreement, which indicates either a broken prompt
/// or a broken calibration set; either is a real bug).</para>
/// </summary>
public sealed class LiveCalibrationTests
{
	/// <summary>
	/// Env var that gates execution. Operators export <c>AILIB_LIVE_CALIBRATION=1</c>
	/// (or any value other than empty / "0" / "false") to opt in.
	/// </summary>
	public const string OptInEnvVar = "AILIB_LIVE_CALIBRATION";

	/// <summary>
	/// Env var pointing to the directory where the JSON report should
	/// be written. Defaults to the test base directory when unset.
	/// </summary>
	public const string ReportDirectoryEnvVar = "AILIB_CALIBRATION_REPORT_DIR";

	private readonly Xunit.Abstractions.ITestOutputHelper _output;

	public LiveCalibrationTests(Xunit.Abstractions.ITestOutputHelper output)
	{
		_output = output;
	}

	[SkippableFact]
	public async Task RunCalibration_against_live_grader_writes_report()
	{
		Skip.IfNot(IsLiveOptedIn(), "AILIB_LIVE_CALIBRATION is not set; live calibration skipped.");

		var providerOptions = AzureOpenAiHttpChatProviderOptions.FromEnvironment();
		Skip.If(providerOptions is null,
			"Azure OpenAI env vars (AZURE_OPENAI_ENDPOINT / _API_KEY / _CHAT_DEPLOYMENT) not all set; live calibration skipped.");

		var calibrationDir = Path.Combine(
			AppContext.BaseDirectory,
			"golden-sets",
			"calibration");
		var cases = CalibrationCaseLoader.LoadAll(calibrationDir);
		cases.Should().NotBeEmpty(
			"the calibration set must ship the 20 starter YAMLs alongside this test assembly; "
			+ "check the csproj's CopyToOutputDirectory entry for golden-sets/**.");

		using var http = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(60),
		};
		var chat = new AzureOpenAiHttpChatProvider(http, providerOptions!);
		var grader = new LlmClaimGrader(
			chat: chat,
			options: Options.Create(new LlmClaimGraderOptions
			{
				// The grader's Model field maps to the gateway's model id,
				// not to a deployment name. This shim ignores the model
				// and uses the deployment from env vars, so we can set
				// anything here.
				Model = providerOptions!.ChatDeployment!,
				MaxTokens = 256,
			}),
			logger: NullLogger<LlmClaimGrader>.Instance);

		var report = await CalibrationRunner.RunAsync(cases, grader);

		report.Outcomes.Should().HaveCount(cases.Count);
		report.CohenKappa.Should().BeGreaterThanOrEqualTo(0.0,
			"κ < 0 means less-than-chance agreement -- either the prompt or the calibration set is broken.");

		// Persist for the workflow's parse step.
		var reportDir = Environment.GetEnvironmentVariable(ReportDirectoryEnvVar);
		var outDir = string.IsNullOrWhiteSpace(reportDir)
			? AppContext.BaseDirectory
			: reportDir;
		Directory.CreateDirectory(outDir);
		var path = Path.Combine(outDir, "calibration-report.json");
		await CalibrationReportWriter.WriteAsync(report, path, DateTimeOffset.UtcNow);

		// The κ band is informational for the test; the workflow gates
		// on the JSON. Surface to test output for debug visibility.
		var band = CalibrationReportWriter.ClassifyBand(report.CohenKappa);
		_output.WriteLine(
			"Live calibration: cases={0} agreement={1:F3} kappa={2:F3} band={3} report={4}",
			report.Outcomes.Count,
			report.ObservedAgreement,
			report.CohenKappa,
			band,
			path);
	}

	private static bool IsLiveOptedIn()
	{
		var raw = Environment.GetEnvironmentVariable(OptInEnvVar);
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		return !raw.Equals("0", StringComparison.Ordinal)
			&& !raw.Equals("false", StringComparison.OrdinalIgnoreCase);
	}
}
