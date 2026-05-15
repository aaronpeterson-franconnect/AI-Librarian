using AiLibrarian.Domain.Skills;

namespace AiLibrarian.IngestWorker;

/// <summary>Phase 1 implementation: explicit skill list (Markdown). Replace with manifest-driven discovery later.</summary>
public sealed class SkillRegistry : ISkillRegistry
{
	private readonly IReadOnlyList<ISkill> _skills;
	private readonly Dictionary<string, ISkill> _byMime;
	private readonly Dictionary<string, ISkill> _byExt;

	public SkillRegistry(IEnumerable<ISkill> skills)
	{
		_skills = skills.ToList();
		_byMime = new Dictionary<string, ISkill>(StringComparer.OrdinalIgnoreCase);
		_byExt = new Dictionary<string, ISkill>(StringComparer.OrdinalIgnoreCase);
		foreach (var skill in _skills)
		{
			foreach (var mime in skill.SupportedMimeTypes)
			{
				_byMime[mime] = skill;
			}

			foreach (var ext in skill.SupportedExtensions)
			{
				_byExt[ext] = skill;
			}
		}
	}

	/// <inheritdoc />
	public IReadOnlyList<ISkill> RegisteredSkills => _skills;

	/// <inheritdoc />
	public ISkill? ResolveByMimeType(string mimeType)
	{
		if (string.IsNullOrWhiteSpace(mimeType))
		{
			return null;
		}

		return _byMime.TryGetValue(mimeType.Trim(), out var s) ? s : null;
	}

	/// <inheritdoc />
	public ISkill? ResolveByExtension(string extension)
	{
		if (string.IsNullOrWhiteSpace(extension))
		{
			return null;
		}

		var e = extension.Trim();
		if (!e.StartsWith('.'))
		{
			e = "." + e;
		}

		return _byExt.TryGetValue(e, out var s) ? s : null;
	}
}
