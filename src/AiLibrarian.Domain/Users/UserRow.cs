namespace AiLibrarian.Domain.Users;

/// <summary>
/// One row in the <c>users</c> table — the database's mirror of an
/// Entra principal per ADR 0005. <see cref="Id"/> equals the Entra OID
/// (the schema makes that explicit), so callers can map between the
/// two without an extra join.
/// </summary>
/// <param name="Id">Entra OID, also the primary key.</param>
/// <param name="Email">Mailable address (citext); null for principals without a mail attribute.</param>
/// <param name="DisplayName">Friendly label rendered in UI; null when no name claim was present.</param>
/// <param name="IsEmployee">False for B2B guests; gates Internal-classification reads via RLS.</param>
/// <param name="DeactivatedAt">Soft-deactivate marker; non-null hides the row from active-user views.</param>
/// <param name="CreatedAt">Provisioning timestamp.</param>
public sealed record UserRow(
	Guid Id,
	string? Email,
	string? DisplayName,
	bool IsEmployee,
	DateTimeOffset? DeactivatedAt,
	DateTimeOffset CreatedAt);
