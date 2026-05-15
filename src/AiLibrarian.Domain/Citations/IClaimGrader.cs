namespace AiLibrarian.Domain.Citations;

/// <summary>
/// LLM-as-judge spot-check grader. The mechanical
/// <see cref="ICitationValidator"/> answers "do the citations look
/// structurally valid"; the grader answers "do the citations
/// substantively support the claim text." It's deliberately separate so
/// validation can be cheap and run on every synthesis, while grading
/// stays expensive and runs on a calibration sample.
/// </summary>
public interface IClaimGrader
{
	/// <summary>
	/// Grade the supplied claim by inspecting its citations' chunk
	/// content. Implementations must call an LLM via the gateway; the
	/// prompt is implementation-defined but should request a structured
	/// verdict so the result is stable.
	/// </summary>
	/// <param name="claim">The claim to grade.</param>
	/// <param name="citedChunkTexts">
	/// The text of each cited chunk, keyed by chunk id. Callers pass
	/// canonicalized chunk text — the grader does not reach back into
	/// storage.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<ClaimGrade> GradeAsync(
		Claim claim,
		IReadOnlyDictionary<Guid, string> citedChunkTexts,
		CancellationToken cancellationToken = default);
}

/// <summary>One grader verdict.</summary>
/// <param name="ClaimId">The claim graded.</param>
/// <param name="Verdict">Supported / NotSupported / Partial / Unverifiable.</param>
/// <param name="Confidence">Grader self-reported confidence in [0, 1].</param>
/// <param name="Rationale">Short prose explanation; safe to surface to a librarian.</param>
public sealed record ClaimGrade(
	Guid ClaimId,
	ClaimVerdict Verdict,
	double Confidence,
	string Rationale);

/// <summary>The four verdict states the grader can return.</summary>
public enum ClaimVerdict
{
	/// <summary>The cited chunks substantively support the claim.</summary>
	Supported = 0,

	/// <summary>Cited chunks do not support the claim.</summary>
	NotSupported = 1,

	/// <summary>Some of the claim is supported; some is not.</summary>
	Partial = 2,

	/// <summary>The grader could not reach a verdict (e.g. cited chunks empty or off-topic).</summary>
	Unverifiable = 3,
}
