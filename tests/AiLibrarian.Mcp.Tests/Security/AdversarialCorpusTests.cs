using AiLibrarian.Security;
using AiLibrarian.Mcp.Tests.Security.Corpus;
using AiLibrarian.Mcp.Tests.Security.Fixtures;

namespace AiLibrarian.Mcp.Tests.Security;

/// <summary>
/// Adversarial-corpus runners. Three sub-corpora:
/// <list type="bullet">
///   <item>Jailbreak prompts — AskGuard must preserve the canonical
///         system prompt verbatim so the LLM has a fighting chance to
///         refuse. (Refusal-rate is a live-LLM property measured by the
///         eval harness; these tests check the mechanical invariants
///         AskGuard owns.)</item>
///   <item>Poisoned chunks — every forged <c>&lt;/source&gt;</c> marker
///         must be neutralized so the chunk cannot break out of the
///         envelope.</item>
///   <item>Secret samples — every sample must produce at least one
///         redaction candidate; enforce mode must strip the credential.</item>
/// </list>
/// </summary>
public sealed class AdversarialCorpusTests
{
	private const string Caller = "00000000-0000-0000-0000-00000000bbbb";

	public static IEnumerable<object[]> AllJailbreaks =>
		AdversarialCorpus.Jailbreaks.Select(j => new object[] { j });

	public static IEnumerable<object[]> AllPoisonedChunks =>
		AdversarialCorpus.PoisonedChunks.Select(p => new object[] { p });

	public static IEnumerable<object[]> AllSecretSamples =>
		AdversarialCorpus.SecretSamples.Select(s => new object[] { s });

	[Theory]
	[MemberData(nameof(AllJailbreaks))]
	public async Task Jailbreak_Query_Preserved_For_LLM_Refusal(string jailbreak)
	{
		var synth = StubSynthesizer.Echo();
		var guard = new AskGuard(
			StubRetrieval.WithSimpleChunk("trusted source content"),
			synth,
			options: Microsoft.Extensions.Options.Options.Create(new AskGuardOptions
			{
				// Cap big enough for every entry in the jailbreak corpus.
				MaxQueryBytes = 8192,
				RateLimitPerMinutePerCaller = 1000,
			}));

		var result = await guard.AskAsync(new AskGuardRequest(Caller, null, jailbreak));

		// AskGuard cannot tell the difference between a jailbreak and a
		// legitimate question -- that's the LLM's call. What it must do:
		// admit the call, preserve the query verbatim for the synthesizer
		// to refuse, and emit a fingerprint of the query for forensics.
		result.Admitted.Should().BeTrue();
		synth.LastRequest!.Query.Should().Be(jailbreak);
		result.AuditFields["query_sha256"].Should().NotBeNull();
	}

	[Theory]
	[MemberData(nameof(AllPoisonedChunks))]
	public async Task Poisoned_Chunk_Forged_Markers_Are_Neutralized(string poisonedText)
	{
		var synth = StubSynthesizer.Echo();
		var retrieval = new StubRetrieval(new[]
		{
			new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "Internal", "engineering", poisonedText),
		});
		var guard = new AskGuard(
			retrieval,
			synth,
			options: Microsoft.Extensions.Options.Options.Create(new AskGuardOptions()));

		await guard.AskAsync(new AskGuardRequest(Caller, null, "hello"));

		var enveloped = synth.LastRequest!.EnvelopedSources;
		// Compute the count of legitimate <source ... > opening tags
		// (one per envelope wrap) and matching closing </source>. Forged
		// markers inside chunk text must NOT add to either count.
		var openCount = CountOccurrences(enveloped, "<source ");
		var closeCount = CountOccurrences(enveloped, "</source>");
		openCount.Should().Be(1, "exactly one legit envelope opening per chunk");
		closeCount.Should().Be(1, "exactly one legit envelope closing per chunk");
	}

	[Theory]
	[MemberData(nameof(AllSecretSamples))]
	public void Secret_Sample_Produces_Redaction_Candidate(string sample)
	{
		var redactor = new SecretRedactor(new AskGuardOptions
		{
			RedactionMode = SecretRedactionMode.Shadow,
		});

		var result = redactor.Scan(sample);
		result.Matches.Should().NotBeEmpty("every corpus entry contains a recognizable credential pattern");
	}

	[Theory]
	[MemberData(nameof(AllSecretSamples))]
	public void Secret_Sample_Enforce_Mode_Strips_Credential(string sample)
	{
		var redactor = new SecretRedactor(new AskGuardOptions
		{
			RedactionMode = SecretRedactionMode.Enforce,
		});

		var result = redactor.Scan(sample);
		result.Output.Should().Contain("[REDACTED:");
	}

	[Fact]
	public void Corpus_Sizes_Meet_Hardening_Plan_Acceptance()
	{
		// "50+ jailbreaks, 50+ poisoned chunks, 30+ secret patterns."
		AdversarialCorpus.Jailbreaks.Length.Should().BeGreaterThanOrEqualTo(50);
		AdversarialCorpus.PoisonedChunks.Length.Should().BeGreaterThanOrEqualTo(50);
		AdversarialCorpus.SecretSamples.Length.Should().BeGreaterThanOrEqualTo(30);
	}

	private static int CountOccurrences(string haystack, string needle)
	{
		var count = 0;
		var idx = 0;
		while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
		{
			count++;
			idx += needle.Length;
		}

		return count;
	}
}
