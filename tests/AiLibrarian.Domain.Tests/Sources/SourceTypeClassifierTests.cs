using AiLibrarian.Domain.Sources;

namespace AiLibrarian.Domain.Tests.Sources;

/// <summary>
/// Pins the classifier's rule cascade. The DB check constraint
/// (migration 0028) is the structural enforcer; these tests assert
/// each rule fires for the inputs it's meant to catch.
/// </summary>
public sealed class SourceTypeClassifierTests
{
	[Theory]
	[InlineData("text/x-python", "deploy.py", null, SourceType.Code)]
	[InlineData("text/plain", "ingest.cs", null, SourceType.Code)]
	[InlineData("text/plain", "Service.go", null, SourceType.Code)]
	[InlineData("application/javascript", null, null, SourceType.Code)]
	public void Code_extensions_and_content_types_classify_as_code(
		string? contentType, string? fileName, string? title, string expected)
	{
		SourceTypeClassifier.Classify(contentType, fileName, title).Should().Be(expected);
	}

	[Theory]
	[InlineData(null, "schema.sql", null)]
	[InlineData("text/plain", "0028-migration.sql", "Source-type column migration")]
	public void Sql_files_classify_as_sql(string? contentType, string? fileName, string? title)
	{
		SourceTypeClassifier.Classify(contentType, fileName, title).Should().Be(SourceType.Sql);
	}

	[Theory]
	[InlineData("image/png", null, null)]
	[InlineData("image/jpeg", "architecture.jpg", null)]
	[InlineData(null, "diagram.svg", null)]
	public void Images_classify_as_image(string? contentType, string? fileName, string? title)
	{
		SourceTypeClassifier.Classify(contentType, fileName, title).Should().Be(SourceType.Image);
	}

	[Theory]
	[InlineData("message/rfc822", null, null)]
	[InlineData(null, "thread.eml", null)]
	[InlineData("application/vnd.ms-outlook", null, null)]
	public void Email_inputs_classify_as_email(string? contentType, string? fileName, string? title)
	{
		SourceTypeClassifier.Classify(contentType, fileName, title).Should().Be(SourceType.Email);
	}

	[Theory]
	[InlineData("application/pdf", null, "Ingest Worker Runbook")]
	[InlineData("text/markdown", "post-mortem-2026-05.md", null)]
	[InlineData("text/markdown", null, "Service Bus incident review")]
	public void Runbook_keywords_in_title_classify_as_runbook(string? contentType, string? fileName, string? title)
	{
		SourceTypeClassifier.Classify(contentType, fileName, title).Should().Be(SourceType.Runbook);
	}

	[Theory]
	[InlineData("text/markdown", null, "JIRA-1234 Investigation")]
	[InlineData("text/markdown", null, "TICKET-77 Failing ingest")]
	public void Ticket_keywords_in_title_classify_as_ticket(string? contentType, string? fileName, string? title)
	{
		SourceTypeClassifier.Classify(contentType, fileName, title).Should().Be(SourceType.Ticket);
	}

	[Theory]
	[InlineData("text/plain", null, "Weekly engineering standup")]
	[InlineData("text/plain", null, "Q3 planning meeting transcript")]
	public void Meeting_keywords_in_title_classify_as_meeting_transcript(string? contentType, string? fileName, string? title)
	{
		SourceTypeClassifier.Classify(contentType, fileName, title).Should().Be(SourceType.MeetingTranscript);
	}

	[Theory]
	[InlineData(null, "page.html", "Confluence export: Ingest")]
	[InlineData("text/html", null, "Wiki: Onboarding")]
	public void Wiki_keywords_in_title_classify_as_wiki_page(string? contentType, string? fileName, string? title)
	{
		SourceTypeClassifier.Classify(contentType, fileName, title).Should().Be(SourceType.WikiPage);
	}

	[Fact]
	public void Unknown_inputs_fall_back_to_document()
	{
		SourceTypeClassifier.Classify(null, null, null).Should().Be(SourceType.Document);
		SourceTypeClassifier.Classify("application/octet-stream", "blob", "Untitled")
			.Should().Be(SourceType.Document);
		SourceTypeClassifier.Classify("application/pdf", "spec.pdf", "Some Spec")
			.Should().Be(SourceType.Document);
	}

	[Fact]
	public void File_extension_outranks_keyword_in_title()
	{
		// A .sql file titled "Runbook for Postgres" should classify as
		// sql, not runbook -- structural signals beat keyword heuristics.
		SourceTypeClassifier.Classify("text/plain", "rotate.sql", "Runbook for Postgres rotation")
			.Should().Be(SourceType.Sql);
	}

	[Fact]
	public void Every_classifier_output_is_in_the_documented_taxonomy()
	{
		// Sample a few realistic inputs and verify every result is one
		// of the documented values -- the DB check constraint would
		// reject anything else.
		var inputs = new[]
		{
			SourceTypeClassifier.Classify("text/x-python", "x.py", null),
			SourceTypeClassifier.Classify(null, "x.sql", null),
			SourceTypeClassifier.Classify("image/png", null, null),
			SourceTypeClassifier.Classify(null, null, "Runbook"),
			SourceTypeClassifier.Classify(null, null, "JIRA-1"),
			SourceTypeClassifier.Classify(null, null, "Meeting notes"),
			SourceTypeClassifier.Classify(null, null, "Wiki: x"),
			SourceTypeClassifier.Classify("message/rfc822", null, null),
			SourceTypeClassifier.Classify(null, null, null),
		};

		inputs.Should().AllSatisfy(t =>
			SourceType.All.Should().Contain(t,
				$"'{t}' must be one of the documented taxonomy values"));
	}
}
