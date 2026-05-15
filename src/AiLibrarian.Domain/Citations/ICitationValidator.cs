namespace AiLibrarian.Domain.Citations;

/// <summary>
/// The contract from ADR 0007: validate that a batch of claims meets
/// every rule before any of them ship to a caller. The validator
/// performs no side effects; it produces a <see cref="CitationValidationResult"/>
/// the caller decides what to do with (refuse the synthesis, downgrade
/// the facet, alert the librarian, …).
/// </summary>
public interface ICitationValidator
{
	/// <summary>
	/// Validate the supplied claims. The implementation resolves chunks
	/// via <see cref="IChunkLookup"/>, applies every <see cref="CitationRule"/>,
	/// and aggregates violations into the result.
	/// </summary>
	/// <param name="claims">Claims to validate; may be empty.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<CitationValidationResult> ValidateAsync(
		IReadOnlyList<Claim> claims,
		CancellationToken cancellationToken = default);
}
