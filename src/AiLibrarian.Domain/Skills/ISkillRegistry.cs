namespace AiLibrarian.Domain.Skills;

/// <summary>
/// Dispatches sources to the correct <see cref="ISkill"/> implementation — ADR 0009.
/// </summary>
public interface ISkillRegistry
{
	IReadOnlyList<ISkill> RegisteredSkills { get; }

	ISkill? ResolveByMimeType(string mimeType);

	ISkill? ResolveByExtension(string extension);
}
