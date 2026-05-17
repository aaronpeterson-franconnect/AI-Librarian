using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AiLibrarian.Security;

/// <summary>
/// Pattern-based secret detector. Defends against ADR 0017 T6 (a
/// credential pasted into a runbook getting re-emitted in synthesis
/// output). Ships in <see cref="SecretRedactionMode.Shadow"/> by
/// default — see ADR 0017 for the precision-sampling protocol that
/// gates enforce-mode flips.
/// </summary>
public sealed class SecretRedactor
{
	// 1 second per pattern is the right budget after the
	// 2026-05-17 flake investigation. The previous 200ms timeout
	// produced CI-only failures because:
	//   - 8 patterns run in parallel via Parallel.ForEach below.
	//   - First call to .Matches() JIT-compiles the Compiled regex.
	//     On a CI shared runner (vs developer hardware), the JIT cost
	//     for some patterns -- particularly the multi-line PEM pattern
	//     with `[\s\S]+?` -- exceeded 200ms.
	//   - The catch (RegexMatchTimeoutException) below silently
	//     swallows the timeout, returning "no matches" -- which a test
	//     asserting "the credential sample SHOULD have produced a
	//     match" then fails.
	// 1 second is still tight enough to defend against catastrophic
	// backtracking on adversarial input (the original concern); regex
	// engines that aren't actively under attack complete in low ms.
	// See AdversarialCorpusTests + AdversarialMetricsTests for the
	// regression coverage.
	private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

	// Order matters: more-specific patterns first so a JWT isn't also
	// flagged as a generic API-key blob.
	private static readonly (string Kind, Regex Pattern)[] Patterns =
	[
		("jwt", new Regex(
			@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
			RegexOptions.Compiled, PatternTimeout)),

		("pem", new Regex(
			"-----BEGIN [A-Z0-9 ]+PRIVATE KEY( BLOCK)?-----[\\s\\S]+?-----END [A-Z0-9 ]+PRIVATE KEY( BLOCK)?-----",
			RegexOptions.Compiled, PatternTimeout)),

		("aws_access_key", new Regex(
			@"\b(AKIA|ASIA)[0-9A-Z]{16}\b",
			RegexOptions.Compiled, PatternTimeout)),

		("github_token", new Regex(
			@"\bghp_[A-Za-z0-9]{20,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b",
			RegexOptions.Compiled, PatternTimeout)),

		("slack_token", new Regex(
			@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b",
			RegexOptions.Compiled, PatternTimeout)),

		("stripe_key", new Regex(
			@"\bsk_(live|test)_[A-Za-z0-9]{16,}\b",
			RegexOptions.Compiled, PatternTimeout)),

		("api_key_assignment", new Regex(
			"(?i)[\"']?\\b(api[_-]?key|api[_-]?secret|secret[_-]?key|access[_-]?token|password)\\b[\"']?\\s*[:=]\\s*[\"']?([A-Za-z0-9+/_\\-=!@#$%^&*]{8,})[\"']?",
			RegexOptions.Compiled, PatternTimeout)),

		("credit_card", new Regex(
			// Luhn check happens in code -- this is just the digit shape.
			@"\b(?:\d[ -]?){13,19}\b",
			RegexOptions.Compiled, PatternTimeout)),
	];

	// Force every Compiled regex through its JIT path at type-load time
	// so the first `Scan` call doesn't pay the cost. Without this, the
	// PatternTimeout above has to absorb both the JIT cost AND the
	// matching cost on a cold pattern -- the original flake source.
	// Match("") is the cheapest possible input that still triggers
	// compilation.
	static SecretRedactor()
	{
		foreach (var (_, pattern) in Patterns)
		{
			try
			{
				pattern.Match(string.Empty);
			}
			catch (RegexMatchTimeoutException)
			{
				// Impossible on empty input; defensive only.
			}
		}
	}

	private readonly AskGuardOptions _options;

	/// <summary>Creates the redactor. <see cref="AskGuardOptions.RedactionMode"/> selects the behavior.</summary>
	public SecretRedactor(AskGuardOptions options)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
	}

	/// <summary>
	/// Scan the supplied text. Returns the redaction outcome — in
	/// <see cref="SecretRedactionMode.Shadow"/> the <see cref="SecretRedactionResult.Output"/>
	/// equals the input; in <see cref="SecretRedactionMode.Enforce"/>
	/// it has matches replaced.
	/// </summary>
	public SecretRedactionResult Scan(string text)
	{
		if (string.IsNullOrEmpty(text) || _options.RedactionMode == SecretRedactionMode.Off)
		{
			return new SecretRedactionResult(text ?? string.Empty, Array.Empty<SecretMatch>(), _options.RedactionMode);
		}

		// Concurrent because the regexes are independent; cheap fan-out.
		var matches = new ConcurrentBag<SecretMatch>();
		Parallel.ForEach(Patterns, kvp =>
		{
			var (kind, pattern) = kvp;
			try
			{
				foreach (Match m in pattern.Matches(text))
				{
					if (!m.Success || m.Length == 0)
					{
						continue;
					}

					if (kind == "credit_card" && !PassesLuhn(m.Value))
					{
						continue;
					}

					matches.Add(new SecretMatch(kind, m.Index, m.Length));
				}
			}
			catch (RegexMatchTimeoutException)
			{
				// Catastrophic backtracking on malicious input: skip this
				// pattern for this scan rather than DOS-ing the caller.
			}
		});

		var list = matches
			.OrderBy(m => m.Index)
			.ThenBy(m => m.Length)
			.ToArray();

		string output;
		if (_options.RedactionMode == SecretRedactionMode.Enforce && list.Length > 0)
		{
			output = Apply(text, list);
		}
		else
		{
			output = text;
		}

		return new SecretRedactionResult(output, list, _options.RedactionMode);
	}

	private static string Apply(string text, IReadOnlyList<SecretMatch> matches)
	{
		// Walk in reverse so earlier offsets stay valid as we splice.
		// Overlapping matches (e.g. an API-key inside a JWT) are deduped
		// by keeping the earlier-listed one (more specific kinds first).
		var resolved = new List<SecretMatch>();
		var coveredUntil = int.MinValue;
		foreach (var m in matches.OrderBy(x => x.Index))
		{
			if (m.Index < coveredUntil)
			{
				continue;
			}

			resolved.Add(m);
			coveredUntil = m.Index + m.Length;
		}

		var sb = new System.Text.StringBuilder(text);
		for (var i = resolved.Count - 1; i >= 0; i--)
		{
			var m = resolved[i];
			sb.Remove(m.Index, m.Length);
			sb.Insert(m.Index, $"[REDACTED:{m.Kind}]");
		}

		return sb.ToString();
	}

	private static bool PassesLuhn(string candidate)
	{
		var digits = candidate.Where(char.IsDigit).ToArray();
		if (digits.Length < 13 || digits.Length > 19)
		{
			return false;
		}

		var sum = 0;
		var doubleIt = false;
		for (var i = digits.Length - 1; i >= 0; i--)
		{
			var d = digits[i] - '0';
			if (doubleIt)
			{
				d *= 2;
				if (d > 9)
				{
					d -= 9;
				}
			}

			sum += d;
			doubleIt = !doubleIt;
		}

		return sum % 10 == 0;
	}
}

/// <summary>Outcome of one <see cref="SecretRedactor.Scan"/> call.</summary>
/// <param name="Output">Possibly-redacted text. Equal to input in Shadow / Off modes.</param>
/// <param name="Matches">Every match found (kind + offset + length).</param>
/// <param name="Mode">The mode the scan ran in (for the audit row).</param>
public sealed record SecretRedactionResult(
	string Output,
	IReadOnlyList<SecretMatch> Matches,
	SecretRedactionMode Mode);

/// <summary>One match in <see cref="SecretRedactionResult.Matches"/>.</summary>
/// <param name="Kind">Stable kind code (<c>jwt</c>, <c>pem</c>, …).</param>
/// <param name="Index">Start offset into the scanned text.</param>
/// <param name="Length">Length of the match.</param>
public sealed record SecretMatch(string Kind, int Index, int Length);
