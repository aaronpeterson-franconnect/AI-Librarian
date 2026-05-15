using AiLibrarian.Domain;
using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Rls;
using AiLibrarian.Infrastructure.Tests.Rls;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace AiLibrarian.Infrastructure.Tests.Persistence;

/// <summary>
/// Pins the source-writer's INSERT path. The structural assertions
/// (RLS predicate, classification + dept stamping) are exercised by
/// the RLS write-matrix battery; this file focuses on the
/// <c>source_type</c> stamping wired in by the
/// <see cref="SourceTypeClassifier"/> integration.
/// </summary>
public sealed class PostgresSourceWriterTests : IClassFixture<RlsPostgresFixture>
{
	private readonly RlsPostgresFixture _fixture;

	public PostgresSourceWriterTests(RlsPostgresFixture fixture)
	{
		_fixture = fixture;
	}

	[SkippableFact]
	public async Task CreateAsync_Stamps_SourceType_From_Classifier()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var ctx = ContributorContext();

		// Title carries "post-mortem" -> classifier returns "runbook".
		var submission = new SourceSubmission(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Classification: Classification.Internal,
			Title: "Service Bus post-mortem 2026-04",
			ContentType: "text/markdown",
			Uri: null,
			ContributedBy: RlsTestData.EngineeringContributorId);

		var id = await writer.CreateAsync(ctx, submission);

		(await SelectScalarAsync<string>(
			"SELECT source_type FROM sources WHERE id = @id",
			("id", id)))
			.Should().Be(SourceType.Runbook);
	}

	[SkippableFact]
	public async Task CreateAsync_Falls_Back_To_Document_When_No_Signals_Match()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		var writer = CreateWriter();
		var ctx = ContributorContext();

		// Nothing classifier-readable matches -> document fallback.
		var submission = new SourceSubmission(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Classification: Classification.Internal,
			Title: "Some generic upload",
			ContentType: "application/pdf",
			Uri: null,
			ContributedBy: RlsTestData.EngineeringContributorId);

		var id = await writer.CreateAsync(ctx, submission);

		(await SelectScalarAsync<string>(
			"SELECT source_type FROM sources WHERE id = @id",
			("id", id)))
			.Should().Be(SourceType.Document);
	}

	[SkippableFact]
	public async Task CreateAsync_Source_Type_Survives_Check_Constraint()
	{
		Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);
		await RlsTestData.SeedIdentitiesAsync(_fixture.ConnectionString);

		// Pin the contract: every classifier output the writer can
		// produce passes the chk_sources_source_type CHECK. The writer
		// only sees (contentType, title) -- no filename column on
		// sources -- so file-extension-only types (Sql, extension-based
		// Code) aren't reachable from upload-time classification in v1
		// and are excluded here. Adding a fileName field to
		// SourceSubmission is a future slice (operators today encode
		// the filename in `title` by convention, which still fires the
		// keyword cascade).
		var writer = CreateWriter();
		var ctx = ContributorContext();

		var perType = new (string Expected, SourceSubmission Submission)[]
		{
			(SourceType.Code, Submit("text/x-python", "deploy.py runtime")),
			(SourceType.Image, Submit("image/png", "Architecture diagram")),
			(SourceType.Email, Submit("message/rfc822", "Customer thread")),
			(SourceType.Runbook, Submit("text/markdown", "Service Bus runbook")),
			(SourceType.Ticket, Submit("text/markdown", "JIRA-1234")),
			(SourceType.MeetingTranscript, Submit("text/plain", "Weekly meeting")),
			(SourceType.WikiPage, Submit("text/html", "Wiki: onboarding")),
			(SourceType.Document, Submit("application/pdf", "Some Spec")),
		};

		foreach (var (expected, submission) in perType)
		{
			var id = await writer.CreateAsync(ctx, submission);
			(await SelectScalarAsync<string>(
				"SELECT source_type FROM sources WHERE id = @id",
				("id", id)))
				.Should().Be(expected,
					$"classifier should output '{expected}' for submission with content_type='{submission.ContentType}' title='{submission.Title}'");
		}
	}

	// --- helpers ---

	private static SourceSubmission Submit(string contentType, string title)
		=> new(
			DepartmentId: RlsTestData.EngineeringDeptId,
			Classification: Classification.Internal,
			Title: title,
			ContentType: contentType,
			Uri: null,
			ContributedBy: RlsTestData.EngineeringContributorId);

	private static RlsSessionContext ContributorContext()
		=> RlsSessionContext.Anonymous() with
		{
			UserId = RlsTestData.EngineeringContributorId,
			IsAuthenticated = true,
			IsEmployee = true,
			ContributorDepartmentIds = new[] { RlsTestData.EngineeringDeptId },
		};

	private PostgresSourceWriter CreateWriter()
	{
		var ds = NpgsqlDataSource.Create(_fixture.ConnectionString);
		return new PostgresSourceWriter(ds, NullLogger<PostgresSourceWriter>.Instance);
	}

	private async Task<T> SelectScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
	{
		await using var conn = new NpgsqlConnection(_fixture.SuperuserConnectionString);
		await conn.OpenAsync();
		await using var cmd = new NpgsqlCommand(sql, conn);
		foreach (var (n, v) in parameters)
		{
			cmd.Parameters.AddWithValue(n, v);
		}
		return (T)(await cmd.ExecuteScalarAsync())!;
	}
}
