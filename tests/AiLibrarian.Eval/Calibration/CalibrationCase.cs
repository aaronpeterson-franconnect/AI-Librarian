using AiLibrarian.Domain.Citations;

namespace AiLibrarian.Eval.Calibration;

/// <summary>
/// One case in the LLM-judge calibration set. Each case is a
/// human-graded (claim, cited-chunks) tuple — the harness runs the LLM
/// judge over the case and compares the verdict to
/// <see cref="HumanVerdict"/> via Cohen's κ. See
/// <c>docs/eval/calibration-rubric.md</c> for grading rules.
///
/// <para>Distinct from <see cref="GoldenCase"/>: a golden case
/// describes a query + expected retrieval / synthesis outcome end-to-end;
/// a calibration case skips the pipeline and grades only the
/// claim-vs-citations relationship.</para>
/// </summary>
/// <param name="Id">Stable identifier; defaults to the YAML file stem.</param>
/// <param name="ClaimText">The claim being graded.</param>
/// <param name="CitedChunks">Chunks the grader sees, in stable order. The runner builds a synthetic <see cref="Claim"/> citing every chunk in this list.</param>
/// <param name="HumanVerdict">The reconciled human verdict — the gold label.</param>
/// <param name="HumanConfidence">Optional human-grader confidence in [0, 1]; carried for triage on disagreements.</param>
/// <param name="HumanRationale">Optional one-sentence prose; carried alongside the verdict for post-mortems.</param>
/// <param name="Tags">Free-form tags (category, difficulty, source area).</param>
public sealed record CalibrationCase(
	string Id,
	string ClaimText,
	IReadOnlyList<CalibrationChunk> CitedChunks,
	ClaimVerdict HumanVerdict,
	double HumanConfidence,
	string HumanRationale,
	IReadOnlyDictionary<string, string> Tags);

/// <summary>One cited chunk in a calibration case.</summary>
/// <param name="ChunkId">Stable chunk id; the runner uses this to key <c>cited_chunk_texts</c> for the grader.</param>
/// <param name="Text">Canonicalized chunk content the grader sees.</param>
public sealed record CalibrationChunk(Guid ChunkId, string Text);
