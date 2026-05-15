namespace AiLibrarian.Domain.Citations;

/// <summary>
/// Outcome of validating one or more <see cref="Claim"/>s. Designed for
/// pivoting: callers can filter by <see cref="CitationRule"/> for "all
/// dangling-citation rows," by claim for "is this answer publishable,"
/// or by chunk for "which chunk turned dangling after a soft-delete."
/// </summary>
/// <param name="Violations">
/// Every rule failure encountered. Empty when all claims pass.
/// </param>
public sealed record CitationValidationResult(
	IReadOnlyList<CitationViolation> Violations)
{
	/// <summary>True when no rule failed.</summary>
	public bool IsValid => Violations.Count == 0;

	/// <summary>Count of distinct claims with at least one violation.</summary>
	public int FailingClaimCount
	{
		get
		{
			var seen = new HashSet<Guid>();
			foreach (var v in Violations)
			{
				seen.Add(v.ClaimId);
			}

			return seen.Count;
		}
	}
}

/// <summary>One rule failure against one citation (or claim, when the rule is "no citation").</summary>
/// <param name="ClaimId">The claim that failed.</param>
/// <param name="CitationId">
/// The citation that failed; null when the rule applies to the claim itself
/// (currently rule 1, "ClaimHasCitation").
/// </param>
/// <param name="Rule">The rule that fired.</param>
/// <param name="Detail">
/// Human-readable detail. Stable enough to log; not parsed by callers
/// (use <see cref="Rule"/> for pivoting).
/// </param>
public sealed record CitationViolation(
	Guid ClaimId,
	Guid? CitationId,
	CitationRule Rule,
	string Detail);
