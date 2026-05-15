using System.Text.Json.Nodes;

namespace AiLibrarian.Domain.Skills;

/// <summary>Persisted retrieval unit; <see cref="SpanAnchor"/> is format-specific JSON per ADR 0009.</summary>
public sealed record Chunk(string ContentMarkdown, JsonNode SpanAnchor, int OrderIndex);
