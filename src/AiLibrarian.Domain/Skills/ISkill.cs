namespace AiLibrarian.Domain.Skills;

/// <summary>
/// File-format plugin contract — ADR 0009. Implementations live in <c>AiLibrarian.Skills.*</c> assemblies.
/// </summary>
public interface ISkill
{
	/// <summary>Stable skill identifier (e.g. <c>markdown</c>).</summary>
	string Name { get; }

	IReadOnlySet<string> SupportedMimeTypes { get; }

	IReadOnlySet<string> SupportedExtensions { get; }

	/// <summary>
	/// Produce canonical markdown, chunks with span anchors, and extracted metadata (e.g. YAML frontmatter).
	/// </summary>
	Task<SkillResult> CanonicalizeAsync(Stream raw, SourceMetadata metadata, CancellationToken cancellationToken);
}
