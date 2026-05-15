using AiLibrarian.Auditing;
using AiLibrarian.Domain.Skills;
using AiLibrarian.Infrastructure.Auditing;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.IngestWorker;
using AiLibrarian.LlmGateway;
using AiLibrarian.Skills.Markdown;
using AiLibrarian.Skills.Office;
using AiLibrarian.Skills.Pdf;

using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

// Phase 1 ingest worker historically read the Postgres connection string from
// IngestWorker:Database:ConnectionString while the API uses ConnectionStrings:Postgres.
// Alias the legacy key onto the standard one so AddPostgresCorpusRepositories /
// AddAiLibrarianAuditing pick it up without forcing an operator-side config rename.
var legacyDbConn = builder.Configuration["IngestWorker:Database:ConnectionString"];
if (!string.IsNullOrWhiteSpace(legacyDbConn)
	&& string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Postgres")))
{
	builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
	{
		["ConnectionStrings:Postgres"] = legacyDbConn,
	});
}

builder.Services.Configure<IngestWorkerOptions>(builder.Configuration.GetSection(IngestWorkerOptions.SectionName));
builder.Services.AddAiLibrarianLlmGateway(builder.Configuration);

// Audit ledger — same shape as the API's composition root, so worker events
// land in audit_events alongside route events with shared correlation IDs.
builder.Services.AddAiLibrarianAuditing(builder.Configuration);

// Read-side + write-side corpus repositories. Falls back to null adapters
// (write-side throws on call) when ConnectionStrings:Postgres is empty.
builder.Services.AddPostgresCorpusRepositories(builder.Configuration);

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Postgres")))
{
	builder.Services.AddSingleton<ISourceChunkPersistence, SourceChunkPersistence>();
}
else
{
	builder.Services.AddSingleton<ISourceChunkPersistence, NullSourceChunkPersistence>();
}

// OpenTelemetry tracing — same canonical sources the API uses, so worker
// spans correlate with route spans through the shared correlation id.
builder.Services
	.AddOpenTelemetry()
	.WithTracing(tracing =>
	{
		tracing
			.AddSource(AiLibActivitySource.Names.All)
			.AddHttpClientInstrumentation();
	});

builder.Services.AddSingleton<ISkillRegistry>(_ => new SkillRegistry(DefaultSkills()));
builder.Services.AddSingleton<IBlobContentOpener, AzureBlobContentOpener>();
builder.Services.AddSingleton<ChunkEmbeddingService>();
builder.Services.AddSingleton<IngestJobPipeline>();
builder.Services.AddHostedService<IngestServiceBusHostedService>();

await builder.Build().RunAsync().ConfigureAwait(false);

static IEnumerable<ISkill> DefaultSkills()
{
	yield return new MarkdownSkill();
	yield return new DocxSkill();
	yield return new XlsxSkill();
	yield return new PptxSkill();
	yield return new PdfSkill();
}
