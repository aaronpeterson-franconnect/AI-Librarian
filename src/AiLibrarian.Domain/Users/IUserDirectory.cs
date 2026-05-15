namespace AiLibrarian.Domain.Users;

/// <summary>
/// Read + just-in-time write side of the <c>users</c> + <c>user_authorizations</c>
/// tables. Two responsibilities:
/// <list type="number">
///   <item><see cref="EnsureUserAsync"/> — provision a <c>users</c> row
///         the first time we see a particular OID. Sign-in lands a user
///         with no row otherwise, which fails the FK on audit writes and
///         leaves RLS unable to resolve them.</item>
///   <item><see cref="GetProjectionAsync"/> — read the user + all their
///         current authorizations for session-context building. Hot path;
///         the implementation should cache per-request to avoid round-
///         tripping per audit + retrieval call.</item>
/// </list>
///
/// <para>The interface lives in Domain because every host that builds
/// an RLS session needs it; the Postgres implementation lives in
/// Infrastructure.</para>
/// </summary>
public interface IUserDirectory
{
	/// <summary>
	/// Upsert the <c>users</c> row for <paramref name="oid"/> based on
	/// claims observed at sign-in. Returns the row that exists after the
	/// call (newly inserted or pre-existing). Subsequent calls with the
	/// same OID return the cached value within the same scope.
	/// </summary>
	/// <param name="oid">The Entra OID; also the database primary key.</param>
	/// <param name="email">Best-known mail address; null is fine.</param>
	/// <param name="displayName">Best-known display name; null is fine.</param>
	/// <param name="isEmployee">
	/// True for first-party / member tokens; false for B2B guest tokens.
	/// Inferred upstream from the <c>idtyp</c> / <c>acct</c> claims; the
	/// caller is the source of truth.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<UserRow> EnsureUserAsync(
		Guid oid,
		string? email,
		string? displayName,
		bool isEmployee,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Fetch the user + every active <see cref="UserAuthorization"/>
	/// grant. Returns null when no <c>users</c> row exists for the OID
	/// (caller decides whether to refuse or to upsert first via
	/// <see cref="EnsureUserAsync"/>).
	/// </summary>
	Task<UserDirectoryProjection?> GetProjectionAsync(
		Guid oid,
		CancellationToken cancellationToken = default);
}
