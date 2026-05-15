namespace AiLibrarian.Domain.Citations;

/// <summary>
/// A claim emitted by synthesis (e.g. an <c>ask</c> answer sentence,
/// a wiki facet bullet). The citation contract from ADR 0007 says
/// every claim is verifiable against the corpus; <see cref="Citations"/>
/// holds the chunks that back the claim, and the
/// <see cref="ICitationValidator"/> proves the contract holds.
/// </summary>
/// <param name="Id">Stable identifier (for joining to a grader's verdict).</param>
/// <param name="Text">The sentence/fragment being asserted.</param>
/// <param name="FacetClassification">
/// The classification ceiling of the surface the claim lives on.
/// A facet rendered at <see cref="Classification.Internal"/> may not
/// cite a <see cref="Classification.Confidential"/> chunk — that would
/// be a leakage path. Enforced by rule 4 in <see cref="CitationRule"/>.
/// </param>
/// <param name="Citations">
/// Zero or more citations backing this claim. Rule 1 says "every claim
/// has at least one citation"; an empty list is a violation, not a
/// dropped-citation edge case.
/// </param>
public sealed record Claim(
	Guid Id,
	string Text,
	Classification FacetClassification,
	IReadOnlyList<Citation> Citations);
