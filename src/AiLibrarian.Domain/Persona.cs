namespace AiLibrarian.Domain;

/// <summary>
/// A persona — the fourth organizing dimension per ADR 0014.
/// A persona shapes retrieval, synthesis, and autonomous-action
/// authority for a defined work-context. <b>Persona is not a
/// visibility dimension.</b>
/// </summary>
/// <param name="Id">Stable identifier; matches <c>personas.id</c>.</param>
/// <param name="Name">Lowercased machine name (e.g., "engineering").</param>
/// <param name="DisplayName">Human-readable name (e.g., "Engineering").</param>
/// <param name="Description">Short description of the work-context.</param>
/// <param name="ClassificationFloor">The lowest classification this persona ever
/// surfaces in retrieval; narrows retrieval, never visibility.</param>
public sealed record Persona(
	Guid Id,
	string Name,
	string DisplayName,
	string Description,
	Classification ClassificationFloor = Classification.Internal);

/// <summary>
/// Mode in which a persona action runs — per ADR 0016.
/// </summary>
public enum PersonaActionMode
{
	/// <summary>Action is disabled for the persona.</summary>
	Off = 0,

	/// <summary>System surfaces a suggestion for a human; the human chooses.</summary>
	Recommend = 1,

	/// <summary>System simulates the decision and logs what it would have done;
	/// no real-world effect. Used to accumulate evidence for promotion.</summary>
	Shadow = 2,

	/// <summary>System effects the action; humans review samples after the fact.
	/// Subject to mandatory periodic sampling per ADR 0016; auto-regresses to
	/// Shadow if sampling lapses for more than 14 days.</summary>
	Autonomous = 3,
}
