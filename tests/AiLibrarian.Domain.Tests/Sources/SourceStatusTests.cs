using AiLibrarian.Domain;
using AiLibrarian.Domain.Sources;

namespace AiLibrarian.Domain.Tests.Sources;

/// <summary>
/// Pin the derived <see cref="Source.Status"/> projection. The
/// architecture documents a richer five-state enum that the schema
/// doesn't yet support; these tests guard the three states we DO
/// model from being silently broken.
/// </summary>
public sealed class SourceStatusTests
{
	private static readonly Guid Dept = Guid.NewGuid();
	private static readonly Guid Contributor = Guid.NewGuid();
	private static readonly DateTimeOffset Now = new(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);

	private static Source Build(
		DateTimeOffset? approvedAt = null,
		DateTimeOffset? softDeletedAt = null) =>
		new(
			Id: Guid.NewGuid(),
			DepartmentId: Dept,
			Classification: Classification.Internal,
			Title: "test",
			Uri: null,
			ContentType: "text/markdown",
			ChecksumSha256: null,
			SizeBytes: null,
			ContributedBy: Contributor,
			ApprovedBy: approvedAt is null ? null : Contributor,
			ApprovedAt: approvedAt,
			SoftDeletedAt: softDeletedAt,
			CreatedAt: Now,
			UpdatedAt: Now);

	[Fact]
	public void Pending_when_neither_approved_nor_deleted()
	{
		Build().Status.Should().Be(SourceStatus.Pending);
	}

	[Fact]
	public void Approved_when_approved_at_set_and_not_deleted()
	{
		Build(approvedAt: Now).Status.Should().Be(SourceStatus.Approved);
	}

	[Fact]
	public void Deleted_takes_priority_over_approved()
	{
		// Soft-deletion is the dominant signal — a soft-deleted approved
		// source must not surface as Approved (RLS hides the row anyway,
		// but the projection should match for any callers reading
		// already-fetched rows).
		Build(approvedAt: Now, softDeletedAt: Now).Status.Should().Be(SourceStatus.Deleted);
	}

	[Fact]
	public void Deleted_when_only_soft_deleted_at_set()
	{
		Build(softDeletedAt: Now).Status.Should().Be(SourceStatus.Deleted);
	}
}
