namespace AiLibrarian.Domain.Skills;

/// <summary>Non-fatal or fatal observation produced while processing a source.</summary>
public sealed record SkillIssue(SkillIssueSeverity Severity, string Message, string? Code = null);
