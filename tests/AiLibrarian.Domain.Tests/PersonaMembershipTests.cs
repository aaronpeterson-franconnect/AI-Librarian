using AiLibrarian.Domain;

namespace AiLibrarian.Domain.Tests;

public sealed class PersonaMembershipTests
{
	[Fact]
	public void IsActiveAt_open_ended_membership_is_always_active()
	{
		var membership = new PersonaMembership(
			UserId: Guid.NewGuid(),
			PersonaId: Guid.NewGuid(),
			DepartmentId: null,
			GrantedAt: DateTimeOffset.UtcNow.AddYears(-1),
			ExpiresAt: null,
			GrantedBy: Guid.NewGuid());

		membership.IsActiveAt(DateTimeOffset.UtcNow).Should().BeTrue();
		membership.IsActiveAt(DateTimeOffset.UtcNow.AddYears(10)).Should().BeTrue();
	}

	[Fact]
	public void IsActiveAt_returns_false_after_expiry()
	{
		var expiry = DateTimeOffset.UtcNow.AddDays(-1);
		var membership = new PersonaMembership(
			UserId: Guid.NewGuid(),
			PersonaId: Guid.NewGuid(),
			DepartmentId: null,
			GrantedAt: DateTimeOffset.UtcNow.AddYears(-1),
			ExpiresAt: expiry,
			GrantedBy: Guid.NewGuid());

		membership.IsActiveAt(DateTimeOffset.UtcNow).Should().BeFalse();
	}

	[Fact]
	public void IsActiveAt_returns_true_before_expiry()
	{
		var expiry = DateTimeOffset.UtcNow.AddDays(7);
		var membership = new PersonaMembership(
			UserId: Guid.NewGuid(),
			PersonaId: Guid.NewGuid(),
			DepartmentId: null,
			GrantedAt: DateTimeOffset.UtcNow.AddDays(-30),
			ExpiresAt: expiry,
			GrantedBy: Guid.NewGuid());

		membership.IsActiveAt(DateTimeOffset.UtcNow).Should().BeTrue();
	}
}
