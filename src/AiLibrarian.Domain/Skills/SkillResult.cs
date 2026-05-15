using System.Text.Json.Nodes;

namespace AiLibrarian.Domain.Skills;

/// <summary>Normalized markdown plus chunks and optional extracted structured metadata.</summary>
public sealed record SkillResult(
	string CanonicalMarkdown,
	IReadOnlyList<Chunk> Chunks,
	IReadOnlyDictionary<string, JsonNode> ExtractedMetadata,
	IReadOnlyList<SkillIssue> Issues);
