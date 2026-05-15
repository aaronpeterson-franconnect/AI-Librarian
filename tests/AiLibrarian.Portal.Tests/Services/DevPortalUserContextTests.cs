using AiLibrarian.Portal.Services;

using Microsoft.Extensions.Options;

namespace AiLibrarian.Portal.Tests.Services;

/// <summary>
/// Dev-mode contributor resolution. Three precedence rules to pin:
///   1. DefaultContributorId wins on initialization.
///   2. Otherwise the first DevContributor row wins.
///   3. Empty roster + empty default leaves the contributor unselected.
/// And SelectContributor mutates the in-circuit selection.
/// </summary>
public sealed class DevPortalUserContextTests
{
	[Fact]
	public void Empty_Options_Leaves_Contributor_Unselected()
	{
		var ctx = new DevPortalUserContext(WrapOptions(new PortalOptions()));

		ctx.EntraEnabled.Should().BeFalse();
		ctx.IsAuthenticated.Should().BeFalse();
		ctx.ContributorId.Should().BeEmpty();
		ctx.DisplayName.Should().BeEmpty();
	}

	[Fact]
	public void DefaultContributorId_Wins_When_Set()
	{
		var opts = new PortalOptions
		{
			DefaultContributorId = "33333333-3333-3333-3333-333333333333",
			DevContributors = new()
			{
				new DevContributor { Id = "11111111-1111-1111-1111-111111111111", DisplayName = "Alice" },
			},
		};

		var ctx = new DevPortalUserContext(WrapOptions(opts));

		ctx.ContributorId.Should().Be("33333333-3333-3333-3333-333333333333");
		ctx.IsAuthenticated.Should().BeTrue();
		// DisplayName falls back to empty when the configured default isn't
		// in the roster -- that's deliberate; the dropdown will show empty.
		ctx.DisplayName.Should().BeEmpty();
	}

	[Fact]
	public void First_DevContributor_Selected_When_No_Default()
	{
		var opts = new PortalOptions
		{
			DevContributors = new()
			{
				new DevContributor { Id = "11111111-1111-1111-1111-111111111111", DisplayName = "Alice" },
				new DevContributor { Id = "22222222-2222-2222-2222-222222222222", DisplayName = "Bob" },
			},
		};

		var ctx = new DevPortalUserContext(WrapOptions(opts));

		ctx.ContributorId.Should().Be("11111111-1111-1111-1111-111111111111");
		ctx.DisplayName.Should().Be("Alice");
	}

	[Fact]
	public void SelectContributor_Mutates_The_Selection()
	{
		var opts = new PortalOptions
		{
			DevContributors = new()
			{
				new DevContributor { Id = "11111111-1111-1111-1111-111111111111", DisplayName = "Alice" },
				new DevContributor { Id = "22222222-2222-2222-2222-222222222222", DisplayName = "Bob" },
			},
		};

		var ctx = new DevPortalUserContext(WrapOptions(opts));
		ctx.SelectContributor("22222222-2222-2222-2222-222222222222");

		ctx.ContributorId.Should().Be("22222222-2222-2222-2222-222222222222");
		ctx.DisplayName.Should().Be("Bob");
	}

	[Fact]
	public void SelectableContributors_Exposes_Roster_Verbatim()
	{
		var opts = new PortalOptions
		{
			DevContributors = new()
			{
				new DevContributor { Id = "aaa", DisplayName = "A" },
				new DevContributor { Id = "bbb", DisplayName = "B" },
			},
		};

		var ctx = new DevPortalUserContext(WrapOptions(opts));

		ctx.SelectableContributors.Should().HaveCount(2);
		ctx.SelectableContributors[0].Id.Should().Be("aaa");
	}

	private static IOptions<PortalOptions> WrapOptions(PortalOptions opts) => Options.Create(opts);
}
