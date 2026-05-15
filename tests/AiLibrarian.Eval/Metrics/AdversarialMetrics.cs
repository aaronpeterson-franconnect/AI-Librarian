using AiLibrarian.Security;

namespace AiLibrarian.Eval.Metrics;

/// <summary>
/// Drives a list of adversarial inputs through <see cref="AskGuard"/>
/// and reports the metrics the hardening plan's acceptance bar names:
/// <list type="bullet">
///   <item>Jailbreak admit rate — share of jailbreak queries that
///         reached the synthesizer (a high rate is fine here; the
///         real refusal happens at the LLM, but a rate change tells
///         us the cap / system prompt drifted).</item>
///   <item>Cross-classification leak rate — share of poisoned chunks
///         whose forged envelope markers escaped neutralization. Must
///         be 0 (any leak fails the gate).</item>
///   <item>Secret redaction candidate rate — share of secret-pattern
///         outputs that produced at least one redaction candidate. CI
///         tracks this for trend; the enforce-mode flip is gated on
///         precision sampling per ADR 0017.</item>
/// </list>
/// </summary>
public static class AdversarialMetrics
{
	/// <summary>
	/// Run a list of jailbreak queries through AskGuard. Returns the
	/// share that were admitted (i.e. AskGuard did not refuse on cap /
	/// rate / no-source) — the LLM is responsible for refusing the
	/// query content itself.
	/// </summary>
	public static async Task<AdversarialJailbreakReport> RunJailbreaksAsync(
		AskGuard guard,
		IReadOnlyList<string> jailbreaks,
		string callerOid,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(guard);
		ArgumentNullException.ThrowIfNull(jailbreaks);

		var admitted = 0;
		var refusedByGuard = 0;
		foreach (var j in jailbreaks)
		{
			var result = await guard
				.AskAsync(new AskGuardRequest(callerOid, null, j), cancellationToken)
				.ConfigureAwait(false);
			if (result.Admitted)
			{
				admitted++;
			}
			else
			{
				refusedByGuard++;
			}
		}

		var total = jailbreaks.Count;
		return new AdversarialJailbreakReport(
			Total: total,
			AdmittedBySocketWithLlmDeciding: admitted,
			RefusedByGuard: refusedByGuard,
			AdmitRate: total == 0 ? 0 : (double)admitted / total);
	}

	/// <summary>
	/// Scan a list of LLM-output samples through <see cref="SecretRedactor"/>
	/// in <see cref="SecretRedactionMode.Shadow"/> mode. Reports the
	/// share that produced at least one candidate — the headline
	/// "redactor catches the leak" signal.
	/// </summary>
	public static AdversarialSecretReport RunSecretSamples(
		SecretRedactor redactor,
		IReadOnlyList<string> samples)
	{
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(samples);

		var withCandidates = 0;
		var totalCandidates = 0;
		foreach (var s in samples)
		{
			var result = redactor.Scan(s);
			if (result.Matches.Count > 0)
			{
				withCandidates++;
			}

			totalCandidates += result.Matches.Count;
		}

		var total = samples.Count;
		return new AdversarialSecretReport(
			Total: total,
			SamplesWithCandidates: withCandidates,
			TotalCandidateMatches: totalCandidates,
			DetectionRate: total == 0 ? 0 : (double)withCandidates / total);
	}
}

/// <summary>Per-batch metrics for the jailbreak adversarial corpus.</summary>
public sealed record AdversarialJailbreakReport(
	int Total,
	int AdmittedBySocketWithLlmDeciding,
	int RefusedByGuard,
	double AdmitRate);

/// <summary>Per-batch metrics for the secret-sample adversarial corpus.</summary>
public sealed record AdversarialSecretReport(
	int Total,
	int SamplesWithCandidates,
	int TotalCandidateMatches,
	double DetectionRate);
