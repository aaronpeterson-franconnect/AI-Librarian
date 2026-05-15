using System.Security.Claims;

using AiLibrarian.Api.Auth;
using AiLibrarian.Domain;
using AiLibrarian.Domain.Users;

using Microsoft.Extensions.Logging.Abstractions;

namespace AiLibrarian.Api.Tests.Auth;

/// <summary>
/// Pins the composition logic: how claims and directory data merge into
/// the per-request <see cref="SessionContextBuilder.SessionContextDto"/>.
/// Three branches matter:
/// <list type="bullet">
///   <item>Unauthenticated principal → no directory call, anonymous dto.</item>
///   <item>Authenticated principal + projection found → dto carries
///         the directory's role lists.</item>
///   <item>Authenticated + JIT fails → falls back to claims-only dto.</item>
/// </list>
/// </summary>
public sealed class SessionContextResolverTests
{
	private static readonly Guid Oid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
	private static readonly Guid DeptEng = Guid.Parse("22222222-2222-2222-2222-222222222222");

	[Fact]
	public async Task Unauthenticated_Principal_Skips_Directory_And_Returns_Anonymous_Dto()
	{
		var directory = new RecordingDirectory();
		var resolver = MakeResolver(directory);

		var user = MakePrincipal(authenticated: false);
		var dto = await resolver.ResolveAsync(user);

		dto.IsAuthenticated.Should().BeFalse();
		dto.UserId.Should().Be(Guid.Empty);
		directory.EnsureCalls.Should().Be(0);
		directory.GetCalls.Should().Be(0);
	}

	[Fact]
	public async Task Authenticated_With_Projection_Populates_Role_Lists()
	{
		var directory = new RecordingDirectory
		{
			ProjectionToReturn = new UserDirectoryProjection(
				User: NewUserRow(isEmployee: true),
				Authorizations: new[]
				{
					new UserAuthorization(Oid, DeptEng, Role.Librarian, "g", DateTimeOffset.UtcNow),
				}),
		};
		var resolver = MakeResolver(directory);

		var user = MakePrincipal(authenticated: true, ("oid", Oid.ToString("D")), ("idtyp", "user"), ("name", "A"));
		var dto = await resolver.ResolveAsync(user);

		dto.IsAuthenticated.Should().BeTrue();
		dto.UserId.Should().Be(Oid);
		dto.HomeDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		dto.LibrarianDepartmentIds.Should().BeEquivalentTo(new[] { DeptEng });
		directory.EnsureCalls.Should().Be(1);
		directory.GetCalls.Should().Be(1);
	}

	[Fact]
	public async Task EnsureUser_Failure_Falls_Back_To_Claims_Only_Without_Throwing()
	{
		var directory = new RecordingDirectory { ThrowOnEnsure = true };
		var resolver = MakeResolver(directory);

		var user = MakePrincipal(authenticated: true, ("oid", Oid.ToString("D")), ("idtyp", "user"));
		var dto = await resolver.ResolveAsync(user);

		// Claims-only fallback: still authenticated, but no role data.
		dto.IsAuthenticated.Should().BeTrue();
		dto.UserId.Should().Be(Oid);
		dto.HomeDepartmentIds.Should().BeEmpty();
		dto.LibrarianDepartmentIds.Should().BeEmpty();
	}

	[Fact]
	public async Task Projection_IsEmployee_Overrides_Claims_When_They_Disagree()
	{
		// Claims may say "user" (employee-like) but the directory has the
		// authoritative is_employee bit. The resolver trusts the directory.
		var directory = new RecordingDirectory
		{
			ProjectionToReturn = new UserDirectoryProjection(
				User: NewUserRow(isEmployee: false),
				Authorizations: Array.Empty<UserAuthorization>()),
		};
		var resolver = MakeResolver(directory);

		var user = MakePrincipal(authenticated: true, ("oid", Oid.ToString("D")), ("idtyp", "user"));
		var dto = await resolver.ResolveAsync(user);

		dto.IsEmployee.Should().BeFalse();
	}

	[Fact]
	public async Task Missing_Oid_Claim_Stays_Anonymous_Even_When_Identity_Authenticated()
	{
		// Edge case: identity says authenticated, but no OID claim was
		// emitted by the IdP. We can't JIT-provision without an OID, so
		// the dto is the no-userId variant.
		var directory = new RecordingDirectory();
		var resolver = MakeResolver(directory);

		var user = MakePrincipal(authenticated: true, ("name", "B"));
		var dto = await resolver.ResolveAsync(user);

		dto.UserId.Should().Be(Guid.Empty);
		directory.EnsureCalls.Should().Be(0, "no OID -> no directory call");
	}

	private static ClaimsPrincipal MakePrincipal(bool authenticated, params (string Type, string Value)[] claims)
	{
		var identity = authenticated
			? new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Test")
			: new ClaimsIdentity();
		return new ClaimsPrincipal(identity);
	}

	private static SessionContextResolver MakeResolver(IUserDirectory directory)
		=> new(directory, NullLogger<SessionContextResolver>.Instance);

	private static UserRow NewUserRow(bool isEmployee) => new(
		Id: Oid,
		Email: "a@b.c",
		DisplayName: "Test",
		IsEmployee: isEmployee,
		DeactivatedAt: null,
		CreatedAt: DateTimeOffset.UtcNow);

	private sealed class RecordingDirectory : IUserDirectory
	{
		public int EnsureCalls { get; private set; }
		public int GetCalls { get; private set; }
		public bool ThrowOnEnsure { get; init; }
		public UserDirectoryProjection? ProjectionToReturn { get; init; }

		public Task<UserRow> EnsureUserAsync(Guid oid, string? email, string? displayName, bool isEmployee, CancellationToken cancellationToken = default)
		{
			EnsureCalls++;
			if (ThrowOnEnsure)
			{
				throw new InvalidOperationException("simulated JIT failure");
			}

			return Task.FromResult(new UserRow(oid, email, displayName, isEmployee, null, DateTimeOffset.UtcNow));
		}

		public Task<UserDirectoryProjection?> GetProjectionAsync(Guid oid, CancellationToken cancellationToken = default)
		{
			GetCalls++;
			return Task.FromResult(ProjectionToReturn);
		}
	}
}
