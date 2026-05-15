using AiLibrarian.Domain;
using AiLibrarian.Domain.Users;

namespace AiLibrarian.Domain.Tests.Users;

/// <summary>
/// The projection's role-roll-up logic. Three properties to pin:
/// <list type="bullet">
///   <item>Reader → only HomeDepartmentIds (covers Reader+).</item>
///   <item>Contributor → both Home and Contributor lists.</item>
///   <item>Librarian → all four lists (since Librarian ≥ Reviewer ≥ Contributor ≥ Reader).</item>
///   <item>Admin → IsAdmin true; no department list entries (Admin grants carry null department).</item>
/// </list>
/// </summary>
public sealed class UserDirectoryProjectionTests
{
	private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid DeptEng = Guid.Parse("22222222-2222-2222-2222-222222222222");
	private static readonly Guid DeptFin = Guid.Parse("33333333-3333-3333-3333-333333333333");

	[Fact]
	public void Reader_Only_Populates_HomeDepartmentIds()
	{
		var p = MakeProjection(
			(DeptEng, Role.Reader));

		p.HomeDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		p.ContributorDepartmentIds.Should().BeEmpty();
		p.ReviewerDepartmentIds.Should().BeEmpty();
		p.LibrarianDepartmentIds.Should().BeEmpty();
		p.IsAdmin.Should().BeFalse();
	}

	[Fact]
	public void Contributor_Populates_Home_And_Contributor()
	{
		var p = MakeProjection(
			(DeptEng, Role.Contributor));

		p.HomeDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		p.ContributorDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		p.ReviewerDepartmentIds.Should().BeEmpty();
		p.LibrarianDepartmentIds.Should().BeEmpty();
	}

	[Fact]
	public void Librarian_Populates_All_Four_Lists()
	{
		var p = MakeProjection(
			(DeptEng, Role.Librarian));

		p.HomeDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		p.ContributorDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		p.ReviewerDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		p.LibrarianDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
	}

	[Fact]
	public void Admin_Sets_IsAdmin_With_No_Department_Entries()
	{
		// Admin grants carry null department_id per the schema.
		var p = new UserDirectoryProjection(
			User: NewUserRow(),
			Authorizations: new[]
			{
				new UserAuthorization(User, DepartmentId: null, Role.Admin, SourceGroupId: "g-admin", GrantedAt: DateTimeOffset.UtcNow),
			});

		p.IsAdmin.Should().BeTrue();
		p.HomeDepartmentIds.Should().BeEmpty();
		p.LibrarianDepartmentIds.Should().BeEmpty();
	}

	[Fact]
	public void MultiDept_Mixed_Roles_Roll_Up_Correctly()
	{
		// User is a Librarian in Engineering AND a Reader in Finance.
		// Eng should appear in all four lists; Finance only in Home.
		var p = MakeProjection(
			(DeptEng, Role.Librarian),
			(DeptFin, Role.Reader));

		p.HomeDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng, DeptFin });
		p.ContributorDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		p.ReviewerDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		p.LibrarianDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
	}

	[Fact]
	public void Duplicate_Grants_Same_Department_Different_Group_Dedupe_By_Department()
	{
		// Two Reader rows for the same dept (driven by two different Entra
		// groups) should produce one HomeDepartmentIds entry.
		var p = new UserDirectoryProjection(
			User: NewUserRow(),
			Authorizations: new[]
			{
				new UserAuthorization(User, DeptEng, Role.Reader, "g-1", DateTimeOffset.UtcNow),
				new UserAuthorization(User, DeptEng, Role.Reader, "g-2", DateTimeOffset.UtcNow),
			});

		p.HomeDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
	}

	private static UserDirectoryProjection MakeProjection(params (Guid dept, Role role)[] grants)
	{
		var auths = grants
			.Select((g, i) => new UserAuthorization(
				UserId: User,
				DepartmentId: g.dept,
				Role: g.role,
				SourceGroupId: $"group-{i}",
				GrantedAt: DateTimeOffset.UtcNow))
			.ToArray();

		return new UserDirectoryProjection(NewUserRow(), auths);
	}

	private static UserRow NewUserRow() => new(
		Id: User,
		Email: "a@b.c",
		DisplayName: "Test",
		IsEmployee: true,
		DeactivatedAt: null,
		CreatedAt: DateTimeOffset.UtcNow);
}
