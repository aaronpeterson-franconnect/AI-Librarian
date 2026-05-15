namespace AiLibrarian.Domain.Personas;

/// <summary>
/// Loads the per-persona retrieval profile that the persona-aware
/// reranker consults. Implementations return
/// <see cref="PersonaRetrievalProfile.Neutral"/> for unknown personas,
/// deactivated personas, or personas whose <c>retrieval_profile</c>
/// JSONB is empty — the caller can stay oblivious to those cases and
/// treat retrieval as "always reranked, but neutral when nothing is
/// configured."
///
/// <para>System-context reader: persona definitions are not RLS-scoped
/// by caller (every authenticated principal can know that the
/// "engineering" persona exists), so the production implementation
/// pushes the system admin context onto its connection.</para>
/// </summary>
public interface IPersonaProfileReader
{
	/// <summary>
	/// Return the retrieval profile for <paramref name="personaId"/>, or
	/// <see cref="PersonaRetrievalProfile.Neutral"/> when no usable
	/// profile is configured.
	/// </summary>
	Task<PersonaRetrievalProfile> GetRetrievalProfileAsync(
		Guid personaId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Return the synthesis style for <paramref name="personaId"/>, or
	/// <see cref="PersonaSynthesisStyle.Neutral"/> when no usable style
	/// is configured. Same tolerant semantics as
	/// <see cref="GetRetrievalProfileAsync"/>: unknown / deactivated /
	/// missing-JSON personas all resolve to neutral.
	/// </summary>
	Task<PersonaSynthesisStyle> GetSynthesisStyleAsync(
		Guid personaId,
		CancellationToken cancellationToken = default);
}
