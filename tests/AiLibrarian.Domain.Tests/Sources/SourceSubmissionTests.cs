using AiLibrarian.Domain;
using AiLibrarian.Domain.Sources;

namespace AiLibrarian.Domain.Tests.Sources;

/// <summary>
/// Pin the <see cref="SourceSubmission"/> shape. The record carries
/// everything <c>ISourceWriter.CreateAsync</c> needs at submission
/// time; SHA-256 and byte size land later via the worker's
/// post-canonicalize update.
/// </summary>
public sealed class SourceSubmissionTests
{
	private static readonly Guid Dept = new("11111111-1111-1111-1111-111111111111");
	private static readonly Guid Contributor = new("22222222-2222-2222-2222-222222222222");

	[Fact]
	public void Records_required_fields()
	{
		var s = new SourceSubmission(
			DepartmentId: Dept,
			Classification: Classification.Internal,
			Title: "Runbook",
			ContentType: "text/markdown",
			Uri: "https://blob/example.md",
			ContributedBy: Contributor);

		s.DepartmentId.Should().Be(Dept);
		s.Classification.Should().Be(Classification.Internal);
		s.Title.Should().Be("Runbook");
		s.ContentType.Should().Be("text/markdown");
		s.Uri.Should().Be("https://blob/example.md");
		s.ContributedBy.Should().Be(Contributor);
	}

	[Fact]
	public void Two_submissions_with_same_data_are_equal()
	{
		var a = new SourceSubmission(Dept, Classification.Internal, "Runbook", "text/markdown", null, Contributor);
		var b = new SourceSubmission(Dept, Classification.Internal, "Runbook", "text/markdown", null, Contributor);

		a.Should().Be(b);
	}

	[Fact]
	public void Different_classification_yields_different_value()
	{
		var a = new SourceSubmission(Dept, Classification.Internal, "Runbook", "text/markdown", null, Contributor);
		var b = a with { Classification = Classification.Confidential };

		a.Should().NotBe(b);
	}
}
