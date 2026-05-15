namespace AiLibrarian.Domain.Users;

/// <summary>
/// A user's row plus every active <see cref="UserAuthorization"/> grant
/// for that user. The shape every API request handler needs to fully
/// build an RLS session context: who you are, plus which departments
/// you can read / contribute to / review / curate, plus the Admin flag.
/// </summary>
/// <param name="User">The user row (<see cref="UserRow"/>).</param>
/// <param name="Authorizations">Every <c>user_authorizations</c> row for this user.</param>
public sealed record UserDirectoryProjection(
	UserRow User,
	IReadOnlyList<UserAuthorization> Authorizations)
{
	/// <summary>True when at least one Admin grant is present.</summary>
	public bool IsAdmin => Authorizations.Any(a => a.Role == Role.Admin);

	/// <summary>
	/// Distinct departments the user has *any* role in (Reader+). Used
	/// to populate <c>app.department_ids</c> for the RLS session.
	/// </summary>
	public IReadOnlyCollection<Guid> HomeDepartmentIds => DistinctDepartments(_ => true);

	/// <summary>Distinct departments where the user has Contributor or higher.</summary>
	public IReadOnlyCollection<Guid> ContributorDepartmentIds =>
		DistinctDepartments(role => role >= Role.Contributor && role <= Role.Librarian);

	/// <summary>Distinct departments where the user has Reviewer or higher.</summary>
	public IReadOnlyCollection<Guid> ReviewerDepartmentIds =>
		DistinctDepartments(role => role >= Role.Reviewer && role <= Role.Librarian);

	/// <summary>Distinct departments where the user has Librarian role.</summary>
	public IReadOnlyCollection<Guid> LibrarianDepartmentIds =>
		DistinctDepartments(role => role == Role.Librarian);

	private HashSet<Guid> DistinctDepartments(Func<Role, bool> roleFilter)
	{
		var set = new HashSet<Guid>();
		foreach (var auth in Authorizations)
		{
			if (auth.DepartmentId is { } dept && roleFilter(auth.Role))
			{
				set.Add(dept);
			}
		}

		return set;
	}
}
