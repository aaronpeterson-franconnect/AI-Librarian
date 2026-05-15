namespace AiLibrarian.Domain.Skills;

/// <summary>Ingest-time metadata supplied by the pipeline before the skill runs.</summary>
public sealed record SourceMetadata(
	string MimeType,
	string? OriginalFileName = null,
	long? ContentLength = null);
