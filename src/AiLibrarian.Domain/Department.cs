namespace AiLibrarian.Domain;

/// <summary>
/// A flat department — the corpus-ownership unit per ADR 0005.
/// No parent/child hierarchy; if a sub-org needs its own boundary,
/// it gets its own row.
/// </summary>
/// <param name="Id">Stable identifier; matches <c>departments.id</c>.</param>
/// <param name="Name">Lowercased machine name (e.g., "engineering").</param>
/// <param name="DisplayName">Human-readable name (e.g., "Engineering").</param>
/// <param name="DeactivatedAt">When the department was deactivated, or null if active.</param>
public sealed record Department(
	Guid Id,
	string Name,
	string DisplayName,
	DateTimeOffset? DeactivatedAt = null)
{
	/// <summary>True when this department is currently active.</summary>
	public bool IsActive => DeactivatedAt is null;
}
