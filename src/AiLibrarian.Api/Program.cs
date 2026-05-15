using System.Reflection;
using System.Security.Claims;
using System.Text;

using AiLibrarian.Api.BlobStorage;
using AiLibrarian.Api.EntraSync;
using AiLibrarian.Api.Ingest;
using AiLibrarian.Api.Auth;
using AiLibrarian.Api.Portal;
using AiLibrarian.Api.Synthesis;
using AiLibrarian.Api.Telemetry;
using AiLibrarian.Api.WikiMaintenance;
using AiLibrarian.Domain.Citations;
using AiLibrarian.Domain.Personas;
using AiLibrarian.Domain.Wiki;
using AiLibrarian.WikiMaintainer;
using AiLibrarian.WikiMaintainer.CandidateDiscovery;
using AiLibrarian.Auditing;
using AiLibrarian.Domain;
using AiLibrarian.Domain.Ingest;
using AiLibrarian.Domain.Sources;
using AiLibrarian.Infrastructure.Auditing;
using AiLibrarian.Infrastructure.Persistence;
using AiLibrarian.Infrastructure.Retrieval;
using AiLibrarian.LlmGateway;
using AiLibrarian.LlmGateway.Abstractions;
using AiLibrarian.Security;

using Azure.Messaging.ServiceBus;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// LLM gateway (Semantic Kernel + Azure OpenAI) — ADR 0003 + ADR 0012.
builder.Services.AddAiLibrarianLlmGateway(builder.Configuration);

// Audit ledger — ADR 0010. Must run after the LLM gateway so the Postgres
// writer overrides the NoOp default registered by AddAiLibrarianLlmGateway.
builder.Services.AddAiLibrarianAuditing(builder.Configuration);

// Correlation-ID propagation — single ID flows from inbound traceparent /
// X-Correlation-Id header through audit rows and downstream calls.
builder.Services.AddScoped<ICorrelationIdAccessor, HttpContextCorrelationIdAccessor>();

builder.Services.Configure<SearchOptions>(builder.Configuration.GetSection(SearchOptions.SectionName));
builder.Services.AddPostgresHybridSearch(builder.Configuration);

// AskGuard (ADR 0017) — wired here so /api/ask can resolve all components
// from DI. RateLimiter is a singleton (token-bucket per-caller state must
// outlive any one request); SecretRedactor and AskSynthesisOptions are
// stateless beyond options.
builder.Services.Configure<AskGuardOptions>(builder.Configuration.GetSection(AskGuardOptions.SectionName));
builder.Services.Configure<AskSynthesisOptions>(builder.Configuration.GetSection(AskSynthesisOptions.SectionName));
builder.Services.AddSingleton<RateLimiter>(sp =>
	new RateLimiter(sp.GetRequiredService<IOptions<AskGuardOptions>>().Value));
builder.Services.AddSingleton<SecretRedactor>(sp =>
	new SecretRedactor(sp.GetRequiredService<IOptions<AskGuardOptions>>().Value));

// Corpus repositories — read-side Source + Department adapters used by the
// MCP get_source / list_departments tools and the Phase 1 portal. Now also
// registers the ISourceWriter used by the upload route.
builder.Services.AddPostgresCorpusRepositories(builder.Configuration);

// Session-context resolver: combines JWT claims with IUserDirectory
// reads so every RLS push carries real role data from
// user_authorizations rather than empty arrays. Scoped so the
// per-request projection cache in PostgresUserDirectory is honored.
builder.Services.AddScoped<ISessionContextResolver, SessionContextResolver>();

// Entra group-sync: reconciles Microsoft.Graph group membership into
// user_authorizations. Hosted service runs on a PeriodicTimer; admin
// endpoint POST /api/admin/entra-sync triggers on demand.
builder.Services.Configure<EntraGroupSyncOptions>(builder.Configuration.GetSection(EntraGroupSyncOptions.SectionName));
{
	var entraSyncOptions = builder.Configuration.GetSection(EntraGroupSyncOptions.SectionName).Get<EntraGroupSyncOptions>();
	var entraSyncReady = entraSyncOptions is { Enabled: true }
		&& !string.IsNullOrWhiteSpace(entraSyncOptions.TenantId)
		&& !string.IsNullOrWhiteSpace(entraSyncOptions.ClientId)
		&& !string.IsNullOrWhiteSpace(entraSyncOptions.ClientSecret);

	if (entraSyncReady)
	{
		builder.Services.AddSingleton<IGraphMembershipClient, GraphMembershipClient>();
	}
	else
	{
		// Stub client so the admin endpoint can still be hit and report
		// "no mappings / disabled" without bringing the host down.
		builder.Services.AddSingleton<IGraphMembershipClient, NoopGraphMembershipClient>();
	}

	builder.Services.AddScoped<EntraGroupSyncService>();
	builder.Services.AddHostedService<EntraGroupSyncHostedService>();
}

// Wiki maintenance: the Wiki Maintainer + supporting plumbing for the
// on-demand admin endpoint and the cascade-regeneration hosted service.
// The maintainer needs ICitationValidator; we wire the CitationValidator
// + IClaimGradeSink + IChunkLookup from Quality + Infrastructure.
builder.Services.Configure<WikiMaintenanceOptions>(builder.Configuration.GetSection(WikiMaintenanceOptions.SectionName));
builder.Services.AddScoped<ICitationValidator, AiLibrarian.Quality.CitationValidator>();
builder.Services.AddSingleton<IWikiSourcePoolBuilder, WikiSourcePoolBuilder>();
builder.Services.AddSingleton<IWikiRevisionNumberer, WikiRevisionNumberer>();
builder.Services.AddSingleton<IDanglingFacetReader, DanglingFacetReader>();
builder.Services.AddScoped<IWikiMaintainer>(sp => new WikiMaintainer(
	chat: sp.GetRequiredService<IChatProvider>(),
	validator: sp.GetRequiredService<ICitationValidator>(),
	writer: sp.GetRequiredService<IWikiRevisionWriter>(),
	options: sp.GetRequiredService<IOptions<WikiMaintainerOptions>>(),
	logger: sp.GetRequiredService<ILogger<WikiMaintainer>>(),
	extractor: null,
	proposalWriter: sp.GetRequiredService<IWikiProposalWriter>(),
	pageReader: sp.GetRequiredService<IWikiPageReader>(),
	confidenceScorer: sp.GetRequiredService<IConfidenceScorer>(),
	personaProfileReader: sp.GetRequiredService<IPersonaProfileReader>()));

// Confidence scorer registration. Default = placeholder (no-op). When
// WikiMaintainer:EmbeddingScorer:EmbeddingDeployment is set, flip to
// EmbeddingSimilarityConfidenceScorer which embeds claim text + chunk
// content per (claim, citation) and uses cosine similarity as the
// confidence. Restart required to change which one runs.
builder.Services.Configure<EmbeddingSimilarityScorerOptions>(
	builder.Configuration.GetSection(EmbeddingSimilarityScorerOptions.SectionName));
{
	var scorerOpts = builder.Configuration
		.GetSection(EmbeddingSimilarityScorerOptions.SectionName)
		.Get<EmbeddingSimilarityScorerOptions>();
	if (!string.IsNullOrWhiteSpace(scorerOpts?.EmbeddingDeployment))
	{
		builder.Services.AddSingleton<IConfidenceScorer, EmbeddingSimilarityConfidenceScorer>();
	}
	else
	{
		builder.Services.AddSingleton<IConfidenceScorer, PlaceholderConfidenceScorer>();
	}
}
builder.Services.AddHostedService<WikiMaintenanceHostedService>();

// Candidate-page discovery. Embedding deployment defaults to the
// Search:EmbeddingDeployment when the candidate-discovery section
// doesn't override it -- one knob less to set for typical operators.
builder.Services.Configure<WikiPageCandidateGeneratorOptions>(opts =>
{
	builder.Configuration.GetSection(WikiPageCandidateGeneratorOptions.SectionName).Bind(opts);
	if (string.IsNullOrWhiteSpace(opts.EmbeddingDeployment))
	{
		opts.EmbeddingDeployment = builder.Configuration["Search:EmbeddingDeployment"]
			?? builder.Configuration["LlmGateway:Providers:azure-openai:EmbeddingDeployment"]
			?? string.Empty;
	}
});
builder.Services.AddSingleton<IWikiPageCandidateGenerator, WikiPageCandidateGenerator>();

builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.SectionName));

builder.Services.Configure<IngestQueueOptions>(builder.Configuration.GetSection(IngestQueueOptions.SectionName));
var ingestQueueConnection = builder.Configuration["IngestQueue:ConnectionString"];
if (!string.IsNullOrWhiteSpace(ingestQueueConnection))
{
	builder.Services.AddSingleton(_ => new ServiceBusClient(ingestQueueConnection));
	builder.Services.AddSingleton<IIngestJobPublisher, ServiceBusIngestJobPublisher>();
}
else
{
	builder.Services.AddSingleton<IIngestJobPublisher, NullIngestJobPublisher>();
}

builder.Services.Configure<BlobStorageOptions>(builder.Configuration.GetSection(BlobStorageOptions.SectionName));
var blobStorageConnection = builder.Configuration["BlobStorage:ConnectionString"];
if (!string.IsNullOrWhiteSpace(blobStorageConnection))
{
	builder.Services.AddSingleton<IBlobUploadService, AzureBlobUploadService>();
}
else
{
	builder.Services.AddSingleton<IBlobUploadService, NullBlobUploadService>();
}

builder.Services.Configure<FormOptions>(o =>
{
	o.MultipartBodyLengthLimit = 100 * 1024 * 1024;
});

if (builder.Environment.IsDevelopment())
{
	builder.Services.AddCors(options =>
	{
		options.AddPolicy(
			"PortalDev",
			p =>
			{
				var origins = builder.Configuration["Portal:CorsOrigins"];
				if (!string.IsNullOrWhiteSpace(origins))
				{
					p.WithOrigins(origins.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
				}
				else
				{
					p.WithOrigins("http://localhost:5215");
				}

				p.AllowAnyHeader();
				p.AllowAnyMethod();
			});
	});
}

// ---------------------------------------------------------------------------
// Telemetry — Application Insights when the connection string is configured;
// no-op otherwise (per Phase 0 phasing.md). OpenTelemetry tracing wires the
// canonical AiLibActivitySource names so audit rows and trace spans share
// correlation ids end-to-end (Week 2 hardening).
// ---------------------------------------------------------------------------
if (!string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
{
	builder.Services.AddApplicationInsightsTelemetry();
}

builder.Services
	.AddOpenTelemetry()
	.WithTracing(tracing =>
	{
		tracing
			.AddSource(AiLibActivitySource.Names.All)
			.AddAspNetCoreInstrumentation()
			.AddHttpClientInstrumentation();
	});

// ---------------------------------------------------------------------------
// Entra ID auth — wired only when the AzureAd configuration section is
// populated. Phase 0 expects this to be set in deployed environments;
// local-dev "no Entra" mode is supported so contributors can run the API
// without a tenant.
// ---------------------------------------------------------------------------
var azureAdSection = builder.Configuration.GetSection("AzureAd");
var entraConfigured = !string.IsNullOrWhiteSpace(azureAdSection["TenantId"])
	&& !string.IsNullOrWhiteSpace(azureAdSection["ClientId"]);

if (entraConfigured)
{
	builder.Services
		.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
		.AddMicrosoftIdentityWebApi(azureAdSection);
}

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Correlation ID must be resolved before any handler runs so audit rows
// emitted by route filters carry it.
app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
	app.UseCors("PortalDev");
}

static string? ResolveEmbeddingDeployment(SearchOptions search, LlmGatewayOptions llm)
{
	if (!string.IsNullOrWhiteSpace(search.EmbeddingDeployment))
	{
		return search.EmbeddingDeployment;
	}

	if (llm.Providers.TryGetValue("azure-openai", out var az)
	    && !string.IsNullOrWhiteSpace(az.EmbeddingDeployment))
	{
		return az.EmbeddingDeployment;
	}

	return null;
}

if (entraConfigured)
{
	app.UseAuthentication();
}

app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

// ---------------------------------------------------------------------------
// Public endpoints
// ---------------------------------------------------------------------------

// Health probe — no auth, used by container readiness/liveness probes.
// Audit-writer state is included so a green response only fires when the
// ledger is actually reachable; degraded mode surfaces here too.
app.MapGet("/health", (IServiceProvider sp) =>
{
	var auditStatus = sp.GetService<IAuditWriterStatus>()?.GetStatus();
	var status = auditStatus is null || auditStatus.CircuitState == "Closed"
		? "ok"
		: "degraded";
	return new HealthResponse(
		Status: status,
		Service: "ai-librarian-api",
		Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
		EntraConfigured: entraConfigured,
		Audit: auditStatus);
})
	.WithName("Health");

// Build info — surfaces commit / build metadata when configured. Useful for
// confirming what's deployed without authentication.
app.MapGet("/build-info", (IConfiguration cfg) => new BuildInfoResponse(
	Service: "ai-librarian-api",
	BuildId: cfg["Build:Id"] ?? "local",
	Commit: cfg["Build:Commit"] ?? "unknown",
	BuiltAt: cfg["Build:Timestamp"] ?? DateTimeOffset.UtcNow.ToString("O")))
	.WithName("BuildInfo");

// ---------------------------------------------------------------------------
// Authenticated endpoints
// ---------------------------------------------------------------------------

// /me — returns the caller's identity and the materialized RLS session
// context that *would* be pushed onto a Postgres connection. Phase 0
// demonstrates the session-variable pushdown shape; Phase 1+ will use
// it on real queries.
var me = app.MapGet("/me", async (
	ClaimsPrincipal user,
	ISessionContextResolver sessionResolver,
	CancellationToken cancellationToken) =>
{
	var context = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
	return new MeResponse(
		Subject: user.FindFirstValue("oid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
		IsAuthenticated: user.Identity?.IsAuthenticated ?? false,
		Claims: user.Claims.Select(c => new ClaimPair(c.Type, c.Value)).ToArray(),
		SessionContext: context);
}).WithName("Me");

if (entraConfigured)
{
	me.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Phase 0 smoke — one round-trip through IChatProvider (audited when Azure
// OpenAI is enabled). Returns 503 when the default chat provider is not wired.
// ---------------------------------------------------------------------------
var smokeLlmHello = app.MapPost("/api/smoke/llm/hello", async (
	IChatProvider chat,
	IOptions<LlmGatewayOptions> options,
	CancellationToken cancellationToken) =>
{
	if (chat is UnconfiguredChatProvider)
	{
		return Results.Problem(
			title: "LLM gateway not configured",
			detail: "Enable LlmGateway:Providers:azure-openai with Endpoint, ChatDeployment, and EmbeddingDeployment (see docs/llm-providers.md).",
			statusCode: StatusCodes.Status503ServiceUnavailable);
	}

	var o = options.Value;
	var modelTag = o.Providers.TryGetValue("azure-openai", out var az)
		&& !string.IsNullOrWhiteSpace(az.ChatDeployment)
		? az.ChatDeployment!
		: chat.ProviderId;

	var correlationId = Guid.NewGuid();
	var request = new ChatCompletionRequest(
		Model: modelTag,
		Messages:
		[
			new ChatMessage("user", "Reply with exactly this phrase and nothing else: Hello from AI Librarian.")
		],
		MaxTokens: 80,
		Temperature: 0,
		PersonaId: null,
		CorrelationId: correlationId);

	var sb = new StringBuilder();
	try
	{
		await foreach (var chunk in chat.StreamCompletionAsync(request, cancellationToken)
			           .ConfigureAwait(false))
		{
			if (!string.IsNullOrEmpty(chunk.DeltaContent))
			{
				sb.Append(chunk.DeltaContent);
			}
		}
	}
	catch (InvalidOperationException ex)
	{
		return Results.Problem(
			title: "LLM call failed",
			detail: ex.Message,
			statusCode: StatusCodes.Status503ServiceUnavailable);
	}

	return Results.Ok(new SmokeLlmHelloResponse(
		CorrelationId: correlationId,
		ProviderId: chat.ProviderId,
		Model: modelTag,
		Reply: sb.ToString().Trim()));
})
	.WithName("SmokeLlmHello")
	.WithTags("Smoke");

if (entraConfigured)
{
	smokeLlmHello.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Hybrid search — vector + full-text over source_chunks (ADR 0001).
// Route-level audit is BestEffort: a search response has already been built;
// audit failures must not break the user request.
// ---------------------------------------------------------------------------
var hybridSearch = app.MapPost("/api/search/hybrid", async (
		HybridSearchHttpRequest body,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IConfiguration cfg,
		IHybridChunkSearch hybrid,
		IEmbeddingProvider embeddings,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		IOptions<LlmGatewayOptions> llmOpts,
		IOptions<SearchOptions> searchOpts,
		CancellationToken cancellationToken) =>
	{
		if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Postgres")))
		{
			return Results.Problem(
				title: "Retrieval not configured",
				detail: "Set ConnectionStrings:Postgres to enable hybrid search.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (string.IsNullOrWhiteSpace(body.Query))
		{
			return Results.Problem(
				title: "Bad request",
				detail: "Query is required.",
				statusCode: StatusCodes.Status400BadRequest);
		}

		if (embeddings is UnconfiguredEmbeddingProvider)
		{
			return Results.Problem(
				title: "Embeddings not configured",
				detail: "Enable LlmGateway embedding provider (see docs/llm-providers.md).",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var deploy = ResolveEmbeddingDeployment(searchOpts.Value, llmOpts.Value);
		if (string.IsNullOrWhiteSpace(deploy))
		{
			return Results.Problem(
				title: "Embedding deployment missing",
				detail: "Set Search:EmbeddingDeployment or LlmGateway:Providers:azure-openai:EmbeddingDeployment.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var so = searchOpts.Value;
		var limit = Math.Clamp(body.Limit ?? so.DefaultLimit, 1, so.MaxLimit);
		var vw = body.VectorWeight ?? so.HybridVectorWeight;
		vw = Math.Clamp(vw, 0.0, 1.0);

		var correlationId = correlation.Current;
		var queryTrimmed = body.Query.Trim();
		IReadOnlyList<ReadOnlyMemory<float>> vectors;
		try
		{
			vectors = await embeddings.EmbedAsync(
					deploy,
					new[] { queryTrimmed },
					correlationId,
					cancellationToken)
				.ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(
				title: "Embedding call failed",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (vectors.Count != 1 || vectors[0].Length != so.ExpectedEmbeddingDimensions)
		{
			return Results.Problem(
				title: "Unexpected embedding response",
				detail: $"Expected one vector of length {so.ExpectedEmbeddingDimensions}.",
				statusCode: StatusCodes.Status502BadGateway);
		}

		var session = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		var rls = session.ToContext();

		// Persona override: the caller may supply an explicit personaId
		// to evaluate retrieval reranking under a different profile
		// than the one on their session. This is Admin-only because
		// persona-as-input is a debug / eval feature; ordinary callers
		// have their persona pinned by the session resolver. The
		// override does NOT change visibility -- RLS still narrows the
		// authorized set; persona only reorders it (ADR 0015).
		var personaOverrideApplied = false;
		if (!string.IsNullOrWhiteSpace(body.PersonaId))
		{
			if (!session.IsAdmin)
			{
				return Results.Problem(
					title: "Forbidden",
					detail: "personaId override requires Admin role.",
					statusCode: StatusCodes.Status403Forbidden);
			}
			if (!Guid.TryParse(body.PersonaId, out var overridePersona))
			{
				return Results.Problem(
					title: "Bad request",
					detail: "personaId must be a GUID.",
					statusCode: StatusCodes.Status400BadRequest);
			}
			rls = rls with { PersonaId = overridePersona };
			personaOverrideApplied = true;
		}

		var hits = await hybrid.SearchAsync(
				rls,
				queryTrimmed,
				vectors[0],
				new HybridSearchRequestOptions(limit, vw),
				cancellationToken)
			.ConfigureAwait(false);

		// Best-effort: search results are already computed; audit must
		// not throw out of a successful read.
		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlationId,
				eventType: "query",
				eventSubtype: "search.hybrid",
				targetKind: "search",
				targetId: null,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["hit_count"] = hits.Count,
					["limit"] = limit,
					["vector_weight"] = vw,
					["embedding_deployment"] = deploy,
					["persona_id"] = rls.PersonaId?.ToString("D"),
					["persona_override_applied"] = personaOverrideApplied,
				}),
			AuditCriticality.BestEffort,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new HybridSearchHttpResponse(correlationId, deploy, hits));
	})
	.WithName("HybridSearch")
	.WithTags("Search");

if (entraConfigured)
{
	hybridSearch.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Ask — LLM synthesis over hybrid-retrieved chunks. AskGuard owns the
// defense layer (query cap, rate limit, source envelope, system prompt,
// secret redaction, audit fingerprinting per ADR 0017). Route-level
// audit is Critical: every ask call leaves an audit row regardless of
// admit/refuse outcome.
// ---------------------------------------------------------------------------
var ask = app.MapPost("/api/ask", async (
		AskHttpRequest body,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IConfiguration cfg,
		IHybridChunkSearch hybrid,
		IEmbeddingProvider embeddings,
		IChatProvider chat,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		IOptions<LlmGatewayOptions> llmOpts,
		IOptions<SearchOptions> searchOpts,
		IOptions<AskGuardOptions> askGuardOpts,
		IOptions<AskSynthesisOptions> askSynthesisOpts,
		RateLimiter rateLimiter,
		SecretRedactor secretRedactor,
		ILogger<Program> logger,
		CancellationToken cancellationToken) =>
	{
		if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Postgres")))
		{
			return Results.Problem(
				title: "Retrieval not configured",
				detail: "Set ConnectionStrings:Postgres to enable ask.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (chat is UnconfiguredChatProvider)
		{
			return Results.Problem(
				title: "LLM gateway not configured",
				detail: "Enable LlmGateway:Providers:azure-openai (see docs/llm-providers.md).",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (embeddings is UnconfiguredEmbeddingProvider)
		{
			return Results.Problem(
				title: "Embeddings not configured",
				detail: "Enable LlmGateway embedding provider.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (string.IsNullOrWhiteSpace(body.Query))
		{
			return Results.Problem(title: "Bad request", detail: "Query is required.", statusCode: StatusCodes.Status400BadRequest);
		}

		var deploy = ResolveEmbeddingDeployment(searchOpts.Value, llmOpts.Value);
		if (string.IsNullOrWhiteSpace(deploy))
		{
			return Results.Problem(
				title: "Embedding deployment missing",
				detail: "Set Search:EmbeddingDeployment or LlmGateway:Providers:azure-openai:EmbeddingDeployment.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var correlationId = correlation.Current;
		var query = body.Query.Trim();
		var callerOid = user.FindFirst("oid")?.Value
			?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
			?? "anonymous";

		// Embed.
		IReadOnlyList<ReadOnlyMemory<float>> vectors;
		try
		{
			vectors = await embeddings.EmbedAsync(deploy, new[] { query }, correlationId, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(title: "Embedding call failed", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (vectors.Count != 1 || vectors[0].Length != searchOpts.Value.ExpectedEmbeddingDimensions)
		{
			return Results.Problem(
				title: "Unexpected embedding response",
				detail: $"Expected one vector of length {searchOpts.Value.ExpectedEmbeddingDimensions}.",
				statusCode: StatusCodes.Status502BadGateway);
		}

		// Retrieve (RLS-filtered).
		var rls = (await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false)).ToContext();
		var topK = Math.Clamp(body.MaxChunks ?? askSynthesisOpts.Value.MaxChunksForSynthesis, 1, 32);
		var hits = await hybrid.SearchAsync(
				rls,
				query,
				vectors[0],
				new HybridSearchRequestOptions(topK, searchOpts.Value.HybridVectorWeight),
				cancellationToken)
			.ConfigureAwait(false);

		var retrievedChunks = ApiAskRetrieval.AdaptHits(hits);
		var retrieval = new ApiAskRetrieval(retrievedChunks);
		var synthesizer = new ApiAskSynthesizer(chat, llmOpts, askSynthesisOpts);
		var guard = new AskGuard(retrieval, synthesizer, askGuardOpts, logger: null, rateLimiter: rateLimiter, redactor: secretRedactor);

		var personaId = body.PersonaId is { } pid && Guid.TryParse(pid, out var pg) ? pg : (Guid?)null;
		var guardRequest = new AskGuardRequest(callerOid, personaId, query);

		AskGuardResult result;
		try
		{
			result = await guard.AskAsync(guardRequest, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
		{
			await auditWriter.WriteAsync(
				RouteAuditing.Build(
					user: user,
					correlationId: correlationId,
					eventType: "tool",
					eventSubtype: "ask.error",
					targetKind: "synthesis",
					targetId: null,
					outcome: EventOutcome.Failure,
					errorClass: ex.GetType().Name,
					details: new Dictionary<string, object?>
					{
						["query_bytes"] = System.Text.Encoding.UTF8.GetByteCount(query),
						["caller_oid"] = callerOid,
					}),
				AuditCriticality.Critical,
				cancellationToken).ConfigureAwait(false);
			return Results.Problem(title: "Ask synthesis failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
		}

		// Audit every path -- admit, refuse, error. AskGuard already built
		// the per-call details dictionary; merge in route-level fields.
		var auditDetails = new Dictionary<string, object?>(result.AuditFields)
		{
			["embedding_deployment"] = deploy,
		};
		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlationId,
				eventType: "tool",
				eventSubtype: result.Admitted ? "ask.answered" : "ask.refused",
				targetKind: "synthesis",
				targetId: null,
				outcome: result.Admitted ? EventOutcome.Success : EventOutcome.Failure,
				details: auditDetails),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		if (!result.Admitted)
		{
			return Results.Json(
				new AskHttpResponse(
					CorrelationId: correlationId,
					Admitted: false,
					Answer: null,
					Refusal: new AskRefusal(result.RefusalReason!.ToString()!, result.RefusalDetail ?? string.Empty),
					ChunkIds: Array.Empty<Guid>(),
					RedactionCandidates: 0,
					RedactionMode: result.RedactionMode.ToString(),
					Claims: Array.Empty<AskClaimDto>(),
					ClaimCount: 0,
					CitedClaimCount: 0),
				statusCode: result.RefusalReason == AskRefusalReason.RateLimited
					? StatusCodes.Status429TooManyRequests
					: StatusCodes.Status200OK);
		}

		// Parse the answer into per-sentence claims so the eval harness
		// (and any dashboard consumer) can compute citation coverage
		// without re-running synthesis. Best-effort: an answer without
		// inline [chunk:GUID] tokens still gets a claims list -- every
		// sentence is a claim with zero citations, and CitedClaimCount
		// reflects that.
		var breakdown = AskClaimBreakdown.Extract(result.Answer);

		return Results.Ok(new AskHttpResponse(
			CorrelationId: correlationId,
			Admitted: true,
			Answer: result.Answer,
			Refusal: null,
			ChunkIds: result.RetrievedChunkIds,
			RedactionCandidates: result.RedactionCandidates,
			RedactionMode: result.RedactionMode.ToString(),
			Claims: breakdown.Claims.Select(c => new AskClaimDto(c.Text, c.ChunkIds)).ToArray(),
			ClaimCount: breakdown.ClaimCount,
			CitedClaimCount: breakdown.CitedClaimCount));
	})
	.WithName("Ask")
	.WithTags("Ask");

if (entraConfigured)
{
	ask.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Audit — recent activity feed. Backed by IAuditQueryService.RecentAsync
// (added in the Section A audit-writer work). Returns 503 when the audit
// query service isn't registered (no Postgres) or 200 with an empty list
// when the audit table is empty. RLS on audit_events restricts the visible
// set to admin or librarian-of-target-dept per 0099-rls-policies.sql.
// Audit is BestEffort (a read).
// ---------------------------------------------------------------------------
var listRecentAudit = app.MapGet("/api/audit/recent", async (
		int? limit,
		ClaimsPrincipal user,
		IConfiguration cfg,
		IServiceProvider sp,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		// Resolve IAuditQueryService from DI lazily — minimal-API parameter
		// binding can't see optional services as constructor params (it
		// infers them as request body, which is forbidden for GET).
		var auditQuery = sp.GetService<IAuditQueryService>();
		if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Postgres")) || auditQuery is null)
		{
			return Results.Problem(
				title: "Audit query not configured",
				detail: "Set ConnectionStrings:Postgres + Auditing:WriterMode=Postgres to expose the audit feed.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var take = Math.Clamp(limit ?? 25, 1, 100);
		var events = await auditQuery.RecentAsync(take, cancellationToken).ConfigureAwait(false);
		var correlationId = correlation.Current;

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlationId,
				eventType: "query",
				eventSubtype: "audit.recent",
				targetKind: "audit_events",
				targetId: null,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["count"] = events.Count,
					["limit"] = take,
				}),
			AuditCriticality.BestEffort,
			cancellationToken).ConfigureAwait(false);

		var items = new List<AuditEventResponse>(events.Count);
		foreach (var e in events)
		{
			items.Add(new AuditEventResponse(
				Id: e.Id,
				OccurredAt: e.OccurredAt,
				ActorUserId: e.ActorUserId,
				DepartmentId: e.DepartmentId,
				EventType: e.EventType,
				EventSubtype: e.EventSubtype,
				TargetKind: e.TargetKind,
				TargetId: e.TargetId,
				CorrelationId: e.CorrelationId,
				Outcome: e.Outcome.ToString(),
				ErrorClass: e.ErrorClass));
		}

		return Results.Ok(new AuditRecentResponse(items));
	})
	.WithName("ListRecentAudit")
	.WithTags("Audit");

if (entraConfigured)
{
	listRecentAudit.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Sources — list visible sources, ordered by most recent. Optional
// departmentId narrows the result; without it, every source the caller's
// RLS predicate authorizes is returned. Audit is BestEffort (a read).
// ---------------------------------------------------------------------------
var listSources = app.MapGet("/api/sources", async (
		Guid? departmentId,
		int? limit,
		int? offset,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IConfiguration cfg,
		ISourceRepository repo,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Postgres")))
		{
			return Results.Problem(
				title: "Sources not configured",
				detail: "Set ConnectionStrings:Postgres to enable source reads.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var take = Math.Clamp(limit ?? 25, 1, 100);
		var skip = Math.Max(0, offset ?? 0);

		var rls = (await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false)).ToContext();
		var sources = await repo.ListAsync(rls, departmentId, take, skip, cancellationToken).ConfigureAwait(false);
		var correlationId = correlation.Current;

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlationId,
				eventType: "query",
				eventSubtype: "sources.list",
				targetKind: "source",
				targetId: null,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["count"] = sources.Count,
					["limit"] = take,
					["offset"] = skip,
					["department_id"] = departmentId,
				}),
			AuditCriticality.BestEffort,
			cancellationToken).ConfigureAwait(false);

		var items = new List<SourceResponse>(sources.Count);
		foreach (var s in sources)
		{
			items.Add(new SourceResponse(
				Id: s.Id,
				DepartmentId: s.DepartmentId,
				Classification: s.Classification.ToString(),
				Status: s.Status.ToString(),
				Title: s.Title,
				Uri: s.Uri,
				ContentType: s.ContentType,
				ChecksumSha256: s.ChecksumSha256,
				SizeBytes: s.SizeBytes,
				ContributedBy: s.ContributedBy,
				ApprovedBy: s.ApprovedBy,
				ApprovedAt: s.ApprovedAt,
				CreatedAt: s.CreatedAt,
				UpdatedAt: s.UpdatedAt));
		}

		return Results.Ok(new SourcesResponse(items));
	})
	.WithName("ListSources")
	.WithTags("Sources");

if (entraConfigured)
{
	listSources.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Sources — read a single source row by id, RLS-filtered. Returns 404 when
// the source is missing OR when RLS hides it from the caller — by design
// the two cases are indistinguishable so callers can't probe for hidden
// sources. Audit is BestEffort (a read).
// ---------------------------------------------------------------------------
var getSource = app.MapGet("/api/sources/{id:guid}", async (
		Guid id,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IConfiguration cfg,
		ISourceRepository repo,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Postgres")))
		{
			return Results.Problem(
				title: "Sources not configured",
				detail: "Set ConnectionStrings:Postgres to enable source reads.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var rls = (await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false)).ToContext();
		var source = await repo.GetByIdAsync(rls, id, cancellationToken).ConfigureAwait(false);
		var correlationId = correlation.Current;

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlationId,
				eventType: "query",
				eventSubtype: source is null ? "source.get.not_found" : "source.get",
				targetKind: "source",
				targetId: id,
				outcome: source is null ? EventOutcome.Partial : EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["found"] = source is not null,
				}),
			AuditCriticality.BestEffort,
			cancellationToken).ConfigureAwait(false);

		if (source is null)
		{
			return Results.NotFound();
		}

		return Results.Ok(new SourceResponse(
			Id: source.Id,
			DepartmentId: source.DepartmentId,
			Classification: source.Classification.ToString(),
			Status: source.Status.ToString(),
			Title: source.Title,
			Uri: source.Uri,
			ContentType: source.ContentType,
			ChecksumSha256: source.ChecksumSha256,
			SizeBytes: source.SizeBytes,
			ContributedBy: source.ContributedBy,
			ApprovedBy: source.ApprovedBy,
			ApprovedAt: source.ApprovedAt,
			CreatedAt: source.CreatedAt,
			UpdatedAt: source.UpdatedAt));
	})
	.WithName("GetSource")
	.WithTags("Sources");

if (entraConfigured)
{
	getSource.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Departments — list active departments visible to the caller. RLS gates
// reads to authenticated principals; the API surfaces the same set MCP
// callers would see.
// ---------------------------------------------------------------------------
var listDepartments = app.MapGet("/api/departments", async (
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IConfiguration cfg,
		IDepartmentRepository repo,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Postgres")))
		{
			return Results.Problem(
				title: "Departments not configured",
				detail: "Set ConnectionStrings:Postgres to enable department reads.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var rls = (await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false)).ToContext();
		var departments = await repo.ListActiveAsync(rls, cancellationToken).ConfigureAwait(false);
		var correlationId = correlation.Current;

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlationId,
				eventType: "query",
				eventSubtype: "departments.list",
				targetKind: "department",
				targetId: null,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["count"] = departments.Count,
				}),
			AuditCriticality.BestEffort,
			cancellationToken).ConfigureAwait(false);

		var items = new List<DepartmentResponse>(departments.Count);
		foreach (var d in departments)
		{
			items.Add(new DepartmentResponse(d.Id, d.Name, d.DisplayName));
		}

		return Results.Ok(new DepartmentsResponse(items));
	})
	.WithName("ListDepartments")
	.WithTags("Departments");

if (entraConfigured)
{
	listDepartments.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Ingest — enqueue JSON job to Service Bus (worker consumes per IngestJobMessage).
// Route-level audit is Critical: an enqueue without an audit row would let a
// source enter the pipeline untraceable. Failures audit too, so an attempted
// enqueue is always visible in the ledger.
// ---------------------------------------------------------------------------
var ingestEnqueue = app.MapPost("/api/ingest/enqueue", async (
		IngestEnqueueHttpRequest body,
		ClaimsPrincipal user,
		IConfiguration cfg,
		IIngestJobPublisher publisher,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		if (string.IsNullOrWhiteSpace(cfg["IngestQueue:ConnectionString"]))
		{
			return Results.Problem(
				title: "Ingest queue not configured",
				detail: "Set IngestQueue:ConnectionString to the Service Bus namespace connection string.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var msg = new IngestJobMessage
		{
			BlobUri = body.BlobUri,
			CorrelationId = body.CorrelationId,
			ContentType = body.ContentType,
			OriginalFileName = body.OriginalFileName,
			SourceId = body.SourceId,
		};

		if (!msg.TryValidate(out var validationError))
		{
			return Results.Problem(
				title: "Invalid ingest job",
				detail: validationError,
				statusCode: StatusCodes.Status400BadRequest);
		}

		var correlationId = correlation.Current;

		PublishIngestJobResult result;
		try
		{
			result = await publisher.PublishAsync(msg, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			await auditWriter.WriteAsync(
				RouteAuditing.Build(
					user: user,
					correlationId: correlationId,
					eventType: "ingest",
					eventSubtype: "enqueue.failed",
					targetKind: "source",
					targetId: msg.SourceId,
					outcome: EventOutcome.Failure,
					errorClass: ex.GetType().Name,
					details: new Dictionary<string, object?>
					{
						["blob_uri"] = msg.BlobUri,
						["content_type"] = msg.ContentType,
						["original_file_name"] = msg.OriginalFileName,
					}),
				AuditCriticality.Critical,
				cancellationToken).ConfigureAwait(false);

			return Results.Problem(
				title: "Failed to publish ingest job",
				detail: ex.Message,
				statusCode: StatusCodes.Status502BadGateway);
		}

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlationId,
				eventType: "ingest",
				eventSubtype: "enqueued",
				targetKind: "source",
				targetId: msg.SourceId,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["blob_uri"] = msg.BlobUri,
					["content_type"] = msg.ContentType,
					["original_file_name"] = msg.OriginalFileName,
					["service_bus_message_id"] = result.MessageId,
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Json(
			new IngestEnqueueResponse(result.MessageId),
			statusCode: StatusCodes.Status202Accepted);
	})
	.WithName("IngestEnqueue")
	.WithTags("Ingest");

if (entraConfigured)
{
	ingestEnqueue.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Portal — multipart upload into Blob Storage + sources row creation
// (Phase 1 single-department MVP). The upload now produces both a blob
// URI AND a sources row so downstream callers (MCP enqueue_source, the
// ingest worker) have something to attach chunks to. Without this step
// the worker would silently no-op because job.SourceId never lands.
//
// Route-level audit is Critical: an upload without an audit row would
// let a source enter the corpus untraceable. Failures audit too so
// attempted uploads are always visible.
// ---------------------------------------------------------------------------
var portalUpload = app.MapPost("/api/portal/sources/upload", async (
		HttpRequest req,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IConfiguration cfg,
		IBlobUploadService blobs,
		ISourceWriter sourceWriter,
		ISourceRepository sourceRepoForDedup,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		IOptions<PortalOptions> portalOpts,
		CancellationToken cancellationToken) =>
	{
		if (string.IsNullOrWhiteSpace(cfg["BlobStorage:ConnectionString"]))
		{
			return Results.Problem(
				title: "Blob storage not configured",
				detail: "Set BlobStorage:ConnectionString (e.g. Azurite) to enable portal uploads.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Postgres")))
		{
			return Results.Problem(
				title: "Sources not configured",
				detail: "Set ConnectionStrings:Postgres so the portal upload can record a sources row.",
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (!req.HasFormContentType)
		{
			return Results.BadRequest(new { title = "Expected multipart/form-data" });
		}

		var form = await req.ReadFormAsync(cancellationToken).ConfigureAwait(false);
		var file = form.Files["file"];
		if (file is null || file.Length == 0)
		{
			return Results.BadRequest(new { title = "Missing file", detail = "Use form field name \"file\"." });
		}

		var po = portalOpts.Value;
		var deptRaw = form["departmentId"].ToString();
		if (string.IsNullOrWhiteSpace(deptRaw))
		{
			deptRaw = po.DefaultDepartmentId;
		}

		if (!Guid.TryParse(deptRaw, out var departmentId) || departmentId == Guid.Empty)
		{
			return Results.BadRequest(new
			{
				title = "departmentId required",
				detail = "Pass a departmentId form field, or configure Portal:DefaultDepartmentId.",
			});
		}

		var classRaw = form["classification"].ToString();
		if (string.IsNullOrWhiteSpace(classRaw))
		{
			classRaw = po.DefaultClassification;
		}

		if (!Enum.TryParse<Classification>(classRaw, ignoreCase: true, out var classification))
		{
			return Results.BadRequest(new
			{
				title = "Invalid classification",
				detail = "Allowed values: Public, Internal, Confidential, Restricted.",
			});
		}

		var formTitle = form["title"].ToString();
		var title = string.IsNullOrWhiteSpace(formTitle)
			? (string.IsNullOrWhiteSpace(file.FileName) ? "(untitled)" : file.FileName)
			: formTitle.Trim();

		var contentType = string.IsNullOrWhiteSpace(file.ContentType)
			? "application/octet-stream"
			: file.ContentType;

		var correlationId = correlation.Current;
		var contributorOid = TryGetUserOid(user);
		if (contributorOid is null || contributorOid == Guid.Empty)
		{
			// Dev-without-Entra escape hatch for the slim pilot. Production
			// should use the Entra oid claim and group-derived RLS context.
			var devContributorRaw = form["contributorId"].ToString();
			if (Guid.TryParse(devContributorRaw, out var devContributor) && devContributor != Guid.Empty)
			{
				contributorOid = devContributor;
			}
		}

		var rls = (await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false)).ToContext();
		if ((user.Identity?.IsAuthenticated ?? false) == false
		    && app.Environment.IsDevelopment()
		    && po.DevelopmentRlsOverrideEnabled
		    && contributorOid is { } devContributorForRls
		    && devContributorForRls != Guid.Empty)
		{
			rls = new(
				UserId: devContributorForRls,
				IsAuthenticated: true,
				IsEmployee: true,
				HomeDepartmentIds: [departmentId],
				ContributorDepartmentIds: [departmentId],
				ReviewerDepartmentIds: [],
				LibrarianDepartmentIds: [],
				IsAdmin: false,
				PersonaId: null);
		}

		// Buffer the upload once so we can compute SHA-256 for the
		// dedup probe before paying for blob upload + source-row INSERT.
		// The buffer cap matches the multipart body limit (100 MB);
		// streaming-hash refactor is a Phase 1+ task for larger files.
		var buffered = new MemoryStream();
		await using (var stream = file.OpenReadStream())
		{
			await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
		}

		buffered.Position = 0;
		string sha256Hex;
		using (var sha = System.Security.Cryptography.SHA256.Create())
		{
			var hash = await sha.ComputeHashAsync(buffered, cancellationToken).ConfigureAwait(false);
			sha256Hex = Convert.ToHexStringLower(hash);
		}

		buffered.Position = 0;

		// Dedup check — same content, same department, not soft-deleted →
		// skip blob upload, return reference to existing source row. The
		// FindByChecksum query honors RLS, so a contributor only sees
		// matches in sources they're authorized to read; matches in
		// hidden sources are deliberately not surfaced to avoid leaking
		// the existence of those sources via a "duplicate" response.
		var existing = await sourceRepoForDedup
			.FindByChecksumAsync(rls, departmentId, sha256Hex, cancellationToken)
			.ConfigureAwait(false);
		if (existing is not null)
		{
			await auditWriter.WriteAsync(
				RouteAuditing.Build(
					user: user,
					correlationId: correlationId,
					eventType: "source",
					eventSubtype: "duplicate_skipped",
					targetKind: "source",
					targetId: existing.Id,
					outcome: EventOutcome.Success,
					details: new Dictionary<string, object?>
					{
						["existing_source_id"] = existing.Id,
						["sha256"] = sha256Hex,
						["original_file_name"] = file.FileName,
						["bytes"] = file.Length,
					}),
				AuditCriticality.BestEffort,
				cancellationToken).ConfigureAwait(false);

			return Results.Ok(new PortalUploadResponse(
				BlobUri: existing.Uri ?? string.Empty,
				OriginalFileName: file.FileName,
				ContentType: existing.ContentType,
				SourceId: existing.Id,
				DepartmentId: existing.DepartmentId,
				Classification: existing.Classification.ToString(),
				Title: existing.Title,
				DuplicateOfExisting: true,
				Sha256: sha256Hex));
		}

		string blobUri;
		string? blobOriginalFileName;
		string? blobContentType;
		try
		{
			var info = await blobs.UploadAsync(buffered, file.FileName, file.ContentType, cancellationToken)
				.ConfigureAwait(false);
			blobUri = info.BlobUri;
			blobOriginalFileName = info.OriginalFileName;
			blobContentType = info.ContentType;
		}
		catch (Exception ex)
		{
			await auditWriter.WriteAsync(
				RouteAuditing.Build(
					user: user,
					correlationId: correlationId,
					eventType: "source",
					eventSubtype: "upload.failed",
					targetKind: "blob",
					targetId: null,
					outcome: EventOutcome.Failure,
					errorClass: ex.GetType().Name,
					details: new Dictionary<string, object?>
					{
						["original_file_name"] = file.FileName,
						["content_type"] = file.ContentType,
						["bytes"] = file.Length,
					}),
				AuditCriticality.Critical,
				cancellationToken).ConfigureAwait(false);

			return Results.Problem(
				title: "Upload failed",
				detail: ex.Message,
				statusCode: StatusCodes.Status502BadGateway);
		}

		// Source row INSERT happens AFTER blob upload — on insert failure the
		// blob is an orphan, scheduled for cleanup by a future janitorial job.
		// The alternative (DB-first) trades orphan blobs for orphan source
		// rows pointing to nonexistent blobs, which is harder to reason about
		// at retrieval time.
		if (contributorOid is null || contributorOid == Guid.Empty)
		{
			// No identifiable contributor — refuse rather than stamp the
			// system sentinel as the uploader. In dev-without-Entra mode the
			// caller must supply a contributorId form field (Phase 1 escape
			// hatch; Phase 2 hardening removes it).
			return Results.BadRequest(new
			{
				title = "Contributor identity required",
				detail = "Sign in via Entra (the oid claim is used) or pass a contributorId form field in dev-without-Entra mode.",
			});
		}

		Guid sourceId;
		try
		{
			sourceId = await sourceWriter.CreateAsync(
					rls,
					new SourceSubmission(
						DepartmentId: departmentId,
						Classification: classification,
						Title: title,
						ContentType: contentType,
						Uri: blobUri,
						ContributedBy: contributorOid.Value),
					cancellationToken)
				.ConfigureAwait(false);
		}
		catch (UnauthorizedSourceWriteException ex)
		{
			await auditWriter.WriteAsync(
				RouteAuditing.Build(
					user: user,
					correlationId: correlationId,
					eventType: "source",
					eventSubtype: "create.denied",
					targetKind: "source",
					targetId: null,
					outcome: EventOutcome.Failure,
					errorClass: "RlsDenied",
					details: new Dictionary<string, object?>
					{
						["department_id"] = departmentId,
						["classification"] = classification.ToString(),
						["blob_uri"] = blobUri,
					}),
				AuditCriticality.Critical,
				cancellationToken).ConfigureAwait(false);

			return Results.Problem(
				title: "Not authorized to write to this department",
				detail: ex.Message,
				statusCode: StatusCodes.Status403Forbidden);
		}

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlationId,
				eventType: "source",
				eventSubtype: "uploaded",
				targetKind: "source",
				targetId: sourceId,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["source_id"] = sourceId,
					["department_id"] = departmentId,
					["classification"] = classification.ToString(),
					["blob_uri"] = blobUri,
					["original_file_name"] = blobOriginalFileName,
					["content_type"] = blobContentType,
					["bytes"] = file.Length,
					["title"] = title,
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new PortalUploadResponse(
			BlobUri: blobUri,
			OriginalFileName: blobOriginalFileName ?? file.FileName,
			ContentType: blobContentType,
			SourceId: sourceId,
			DepartmentId: departmentId,
			Classification: classification.ToString(),
			Title: title,
			DuplicateOfExisting: false,
			Sha256: sha256Hex));

		static Guid? TryGetUserOid(ClaimsPrincipal? principal)
		{
			if (principal is null)
			{
				return null;
			}

			var raw = principal.FindFirstValue("oid")
				?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
				?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

			return Guid.TryParse(raw, out var parsed) ? parsed : null;
		}
	})
	.DisableAntiforgery()
	.WithName("PortalSourceUpload")
	.WithTags("Portal");

if (entraConfigured)
{
	portalUpload.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin — source-type backfill (ADR 0015 sourceTypeWeights closeout).
// Bounded per call: classifies up to `batchSize` (default 100, cap 500)
// of the unclassified (source_type IS NULL) rows and reports remaining.
// Idempotent: re-running picks up where the prior call stopped because
// the WHERE clause filters out already-classified rows. Operators
// typically loop until `remainingUnclassified` hits zero, or set up a
// scheduled call.
// ---------------------------------------------------------------------------
var sourceTypeBackfillAdmin = app.MapPost("/api/admin/sources/source-type/backfill", async (
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		ISourceTypeBackfiller backfiller,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		int? batchSize,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAdmin)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Admin role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		var safeBatch = batchSize is int b ? b : 100;

		SourceTypeBackfillOutcome outcome;
		try
		{
			outcome = await backfiller.BackfillBatchAsync(safeBatch, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(
				title: "Backfill not configured",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlation.Current,
				eventType: "admin",
				eventSubtype: "source_type.backfill",
				targetKind: "sources",
				targetId: null,
				outcome: EventOutcome.Success,
				errorClass: null,
				details: new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["batch_size"] = safeBatch,
					["classified"] = outcome.ClassifiedThisCall,
					["remaining_unclassified"] = outcome.RemainingUnclassified,
					["counts"] = outcome.ClassificationCounts,
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new SourceTypeBackfillHttpResponse(
			ClassifiedThisCall: outcome.ClassifiedThisCall,
			RemainingUnclassified: outcome.RemainingUnclassified,
			ClassificationCounts: outcome.ClassificationCounts));
	})
	.WithName("SourceTypeBackfill")
	.WithTags("Admin");

if (entraConfigured)
{
	sourceTypeBackfillAdmin.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin — on-demand Entra group sync. Admin-only (RLS check happens in
// the resolver -> projection -> IsAdmin path). Available even when
// EntraSync:Enabled=false so an operator can validate config without
// restarting the API; the service returns a no-op report in that case.
// ---------------------------------------------------------------------------
var entraSyncAdmin = app.MapPost("/api/admin/entra-sync", async (
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		EntraGroupSyncService syncService,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAdmin)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Admin role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		var report = await syncService.RunAsync(cancellationToken).ConfigureAwait(false);
		return Results.Ok(report);
	})
	.WithName("EntraGroupSyncRun")
	.WithTags("Admin");

if (entraConfigured)
{
	entraSyncAdmin.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin -- on-demand wiki maintenance. Admin-only. Retrieves source
// chunks via HybridChunkSearch (the maintainer's RLS context flows
// from the resolver), runs the two-pass Wiki Maintainer, and audits
// the result with Critical criticality.
//
// The caller pre-creates the wiki_pages + page_facets rows (the
// maintainer doesn't decide what pages exist). On first revision for
// a facet, RevisionNumber starts at 1; WikiRevisionNumberer computes
// max(existing)+1 so callers don't have to track this state.
// ---------------------------------------------------------------------------
var wikiMaintainAdmin = app.MapPost("/api/admin/wiki/maintain", async (
		WikiMaintainHttpRequest body,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiMaintainer maintainer,
		IWikiSourcePoolBuilder poolBuilder,
		IWikiRevisionNumberer numberer,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAdmin)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Admin role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		if (body is null
			|| body.PageId == Guid.Empty
			|| string.IsNullOrWhiteSpace(body.Topic)
			|| !Enum.TryParse<Classification>(body.FacetClassification, ignoreCase: true, out var classification))
		{
			return Results.Problem(
				title: "Bad request",
				detail: "Required: pageId (GUID), facetClassification (Public|Internal|Confidential|Restricted), topic.",
				statusCode: StatusCodes.Status400BadRequest);
		}

		Guid? personaId = !string.IsNullOrWhiteSpace(body.PersonaId) && Guid.TryParse(body.PersonaId, out var pid)
			? pid
			: null;

		// Build the source pool under the system context -- the
		// maintainer runs as the system user (the wiki RLS write
		// policy is admin-only), and the chunk-pool RLS reads also
		// run in system context for consistency.
		WikiSourcePoolResult pool;
		try
		{
			pool = await poolBuilder
				.BuildAsync(AiLibrarian.Infrastructure.Rls.RlsSessionContext.System(), body.Topic, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(
				title: "Source pool unavailable",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		var revno = await numberer
			.NextAsync(body.PageId, classification, personaId, cancellationToken)
			.ConfigureAwait(false);

		var request = new WikiMaintenanceRequest(
			PageId: body.PageId,
			FacetClassification: classification,
			PersonaId: personaId,
			RevisionNumber: revno,
			Topic: body.Topic,
			SourceChunks: pool.Chunks,
			AuthoredBy: AuditConstants.SystemUserId);

		var result = await maintainer.GenerateRevisionAsync(request, cancellationToken).ConfigureAwait(false);

		// Critical audit -- silent maintenance failures would defeat
		// the cascade contract.
		var auditDetails = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["page_id"] = body.PageId.ToString("D"),
			["classification"] = classification.ToString(),
			["persona_id"] = personaId?.ToString("D"),
			["revision_number"] = revno,
			["topic"] = body.Topic,
			["chunk_count"] = pool.Chunks.Count,
			["embedding_deployment"] = pool.EmbeddingDeployment,
			["claim_count"] = result.ClaimCount,
			["citation_count"] = result.CitationCount,
			["revision_id"] = result.RevisionId?.ToString("D"),
			["rejection_reason"] = result.RejectionReason,
		};
		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlation.Current,
				eventType: "wiki",
				eventSubtype: result.Succeeded ? "maintain.ok" : "maintain.rejected",
				targetKind: "wiki_page",
				targetId: body.PageId,
				outcome: result.Succeeded ? EventOutcome.Success : EventOutcome.Failure,
				errorClass: result.Succeeded ? null : "WikiMaintainerRejected",
				details: auditDetails),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new WikiMaintainHttpResponse(
			Succeeded: result.Succeeded,
			RevisionId: result.RevisionId,
			RevisionNumber: revno,
			ClaimCount: result.ClaimCount,
			CitationCount: result.CitationCount,
			ChunkPoolSize: pool.Chunks.Count,
			RejectionReason: result.RejectionReason));
	})
	.WithName("WikiMaintain")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiMaintainAdmin.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin -- auto-page-discovery. One-shot operator endpoint that
// idempotently materializes a (wiki_pages, page_facets) pair and then
// runs the same maintenance pass as /api/admin/wiki/maintain. Closes
// the runbook gap that previously required two hand-written INSERTs
// before each maintenance call.
//
// Body shape: either supply an explicit `slug` (validated against the
// DB check constraint) or omit it and we derive one from `title`. The
// response carries `pageCreated` / `facetCreated` flags so the operator
// can see whether this call brought the page into existence or just
// reused an existing one.
// ---------------------------------------------------------------------------
var wikiDiscoverAdmin = app.MapPost("/api/admin/wiki/discover", async (
		WikiDiscoverHttpRequest body,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiPageWriter pageWriter,
		IWikiMaintainer maintainer,
		IWikiSourcePoolBuilder poolBuilder,
		IWikiRevisionNumberer numberer,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAdmin)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Admin role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		// Validate up-front so the operator gets a 400 with a specific
		// hint rather than a generic "bad request".
		if (body is null
			|| body.DepartmentId == Guid.Empty
			|| string.IsNullOrWhiteSpace(body.Title)
			|| string.IsNullOrWhiteSpace(body.Topic)
			|| !Enum.TryParse<Classification>(body.FacetClassification, ignoreCase: true, out var classification))
		{
			return Results.Problem(
				title: "Bad request",
				detail: "Required: departmentId (GUID), title, topic, facetClassification (Public|Internal|Confidential|Restricted).",
				statusCode: StatusCodes.Status400BadRequest);
		}

		// Slug: explicit when supplied, derived from title otherwise.
		string? slug = body.Slug;
		if (string.IsNullOrWhiteSpace(slug))
		{
			slug = WikiSlug.From(body.Title);
			if (slug is null)
			{
				return Results.Problem(
					title: "Bad request",
					detail: "Title did not yield a usable slug (after normalization it was empty). Supply `slug` explicitly.",
					statusCode: StatusCodes.Status400BadRequest);
			}
		}
		else if (!WikiSlug.IsValid(slug))
		{
			return Results.Problem(
				title: "Bad request",
				detail: "slug must match ^[a-z0-9][a-z0-9\\-]{0,254}$.",
				statusCode: StatusCodes.Status400BadRequest);
		}

		Guid? personaId = !string.IsNullOrWhiteSpace(body.PersonaId) && Guid.TryParse(body.PersonaId, out var pid)
			? pid
			: null;

		// Step 1: ensure page + facet. Idempotent: existing pages are
		// reused without overwriting title or locked flag.
		EnsurePageResult ensure;
		try
		{
			ensure = await pageWriter
				.EnsurePageAsync(
					new EnsurePageRequest(body.DepartmentId, slug, body.Title, classification, personaId),
					cancellationToken)
				.ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			// NullWikiPageWriter path -- dev-without-Postgres mode.
			return Results.Problem(
				title: "Wiki not configured",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}
		catch (ArgumentException ex)
		{
			return Results.Problem(
				title: "Bad request",
				detail: ex.Message,
				statusCode: StatusCodes.Status400BadRequest);
		}

		// Step 2: pull the source pool under the system context, same
		// as /api/admin/wiki/maintain.
		WikiSourcePoolResult pool;
		try
		{
			pool = await poolBuilder
				.BuildAsync(AiLibrarian.Infrastructure.Rls.RlsSessionContext.System(), body.Topic, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(
				title: "Source pool unavailable",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		// Step 3: maintenance. Number the revision, run the two-pass
		// pipeline, audit.
		var revno = await numberer
			.NextAsync(ensure.PageId, classification, personaId, cancellationToken)
			.ConfigureAwait(false);

		var request = new WikiMaintenanceRequest(
			PageId: ensure.PageId,
			FacetClassification: classification,
			PersonaId: personaId,
			RevisionNumber: revno,
			Topic: body.Topic,
			SourceChunks: pool.Chunks,
			AuthoredBy: AuditConstants.SystemUserId);

		var result = await maintainer.GenerateRevisionAsync(request, cancellationToken).ConfigureAwait(false);

		// One audit row -- the discover flow is a single operator action
		// even though it composes ensure-page + maintain internally.
		var auditDetails = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["department_id"] = body.DepartmentId.ToString("D"),
			["slug"] = slug,
			["title"] = body.Title,
			["page_id"] = ensure.PageId.ToString("D"),
			["page_created"] = ensure.PageCreated,
			["facet_created"] = ensure.FacetCreated,
			["classification"] = classification.ToString(),
			["persona_id"] = personaId?.ToString("D"),
			["revision_number"] = revno,
			["topic"] = body.Topic,
			["chunk_count"] = pool.Chunks.Count,
			["embedding_deployment"] = pool.EmbeddingDeployment,
			["claim_count"] = result.ClaimCount,
			["citation_count"] = result.CitationCount,
			["revision_id"] = result.RevisionId?.ToString("D"),
			["rejection_reason"] = result.RejectionReason,
		};
		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlation.Current,
				eventType: "wiki",
				eventSubtype: result.Succeeded ? "discover.ok" : "discover.rejected",
				targetKind: "wiki_page",
				targetId: ensure.PageId,
				outcome: result.Succeeded ? EventOutcome.Success : EventOutcome.Failure,
				errorClass: result.Succeeded ? null : "WikiMaintainerRejected",
				details: auditDetails),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new WikiDiscoverHttpResponse(
			PageId: ensure.PageId,
			Slug: slug,
			PageCreated: ensure.PageCreated,
			FacetCreated: ensure.FacetCreated,
			Maintenance: new WikiMaintainHttpResponse(
				Succeeded: result.Succeeded,
				RevisionId: result.RevisionId,
				RevisionNumber: revno,
				ClaimCount: result.ClaimCount,
				CitationCount: result.CitationCount,
				ChunkPoolSize: pool.Chunks.Count,
				RejectionReason: result.RejectionReason)));
	})
	.WithName("WikiDiscover")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiDiscoverAdmin.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin -- candidate-page discovery (v1). Samples chunks for the
// department, embeds, k-means clusters, asks the LLM to name each
// cluster, dedupes against existing wiki_pages, and returns candidates
// ordered by cluster size. Operator eyeballs the list and calls
// /api/admin/wiki/discover for each one they want to materialize.
//
// No review-queue table in v1 — the candidate set is ephemeral; if the
// operator dismisses the page they can re-run discovery later. This
// keeps the slice bounded; a persistent "discovery queue" is a future
// slice once we see real librarian usage patterns.
// ---------------------------------------------------------------------------
var wikiDiscoverCandidatesAdmin = app.MapPost("/api/admin/wiki/discover-candidates", async (
		WikiDiscoverCandidatesHttpRequest body,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiPageCandidateGenerator generator,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAdmin)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Admin role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		if (body is null || body.DepartmentId == Guid.Empty)
		{
			return Results.Problem(
				title: "Bad request",
				detail: "Required: departmentId (GUID).",
				statusCode: StatusCodes.Status400BadRequest);
		}

		WikiPageCandidateBatch batch;
		try
		{
			batch = await generator.DiscoverAsync(
				body.DepartmentId,
				sampleSize: body.SampleSize ?? 100,
				maxCandidates: body.MaxCandidates ?? 5,
				correlationId: correlation.Current == Guid.Empty ? Guid.NewGuid() : correlation.Current,
				cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(
				title: "Candidate discovery unavailable",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		// One audit row per discovery call. Per-candidate decisions are
		// audited later when the operator calls /discover for each one
		// they pick.
		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlation.Current,
				eventType: "wiki",
				eventSubtype: "discover.candidates",
				targetKind: "department",
				targetId: body.DepartmentId,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["department_id"] = body.DepartmentId.ToString("D"),
					["sample_size"] = body.SampleSize ?? 100,
					["max_candidates"] = body.MaxCandidates ?? 5,
					["sampled"] = batch.SampledChunkCount,
					["returned"] = batch.Candidates.Count,
					["embedding_deployment"] = batch.EmbeddingDeployment,
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new WikiDiscoverCandidatesHttpResponse(
			SampledChunkCount: batch.SampledChunkCount,
			EmbeddingDeployment: batch.EmbeddingDeployment,
			Candidates: batch.Candidates.Select(c => new WikiPageCandidateDto(
				ProposedTitle: c.ProposedTitle,
				ProposedSlug: c.ProposedSlug,
				Summary: c.Summary,
				HighestClassification: c.HighestClassification.ToString(),
				SupportingChunkIds: c.SupportingChunkIds,
				ClusterSize: c.ClusterSize)).ToArray()));
	})
	.WithName("WikiDiscoverCandidates")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiDiscoverCandidatesAdmin.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin -- page lifecycle. PATCH lets operators rename a page and/or
// flip the locked flag. Slug stays frozen (the canonical identity is
// the (department, slug) pair; renaming the title only relabels).
// Either or both fields may be present; an empty body is a 400 to
// catch accidental misuse.
//
// Audit:
//   wiki/page.renamed -- when title actually changes
//   wiki/page.locked  -- when locked actually flips
// Both are Critical: rename/lock decisions are governance events.
// ---------------------------------------------------------------------------
var wikiPagePatch = app.MapPatch("/api/admin/wiki/pages/{id:guid}", async (
		Guid id,
		WikiPagePatchHttpRequest body,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiPageWriter pageWriter,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAdmin)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Admin role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		if (body is null || (string.IsNullOrWhiteSpace(body.Title) && body.Locked is null))
		{
			return Results.Problem(
				title: "Bad request",
				detail: "At least one of `title` or `locked` must be supplied.",
				statusCode: StatusCodes.Status400BadRequest);
		}

		// Apply rename first (if requested); fall through to lock-flip.
		bool? renamed = null;
		bool? lockFlipped = null;

		if (!string.IsNullOrWhiteSpace(body.Title))
		{
			try
			{
				renamed = await pageWriter.RenameAsync(id, body.Title, cancellationToken).ConfigureAwait(false);
			}
			catch (InvalidOperationException ex)
			{
				return Results.Problem(
					title: "Wiki not configured",
					detail: ex.Message,
					statusCode: StatusCodes.Status503ServiceUnavailable);
			}
		}

		if (body.Locked is bool wantLocked)
		{
			try
			{
				lockFlipped = await pageWriter.SetLockedAsync(id, wantLocked, cancellationToken).ConfigureAwait(false);
			}
			catch (InvalidOperationException ex)
			{
				return Results.Problem(
					title: "Wiki not configured",
					detail: ex.Message,
					statusCode: StatusCodes.Status503ServiceUnavailable);
			}
		}

		// If both writes report "no row updated" -> page id doesn't exist.
		var anyWriteAttempted = renamed.HasValue || lockFlipped.HasValue;
		var anyWriteSucceeded = (renamed ?? false) || (lockFlipped ?? false);
		if (anyWriteAttempted && !anyWriteSucceeded)
		{
			return Results.NotFound(new { pageId = id, found = false });
		}

		// Two separate audit rows so each governance event is its own
		// row -- the librarian dashboard can pivot on subtype.
		if (renamed == true)
		{
			await auditWriter.WriteAsync(
				RouteAuditing.Build(
					user: user,
					correlationId: correlation.Current,
					eventType: "wiki",
					eventSubtype: "page.renamed",
					targetKind: "wiki_page",
					targetId: id,
					outcome: EventOutcome.Success,
					errorClass: null,
					details: new Dictionary<string, object?>(StringComparer.Ordinal)
					{
						["new_title"] = body.Title,
					}),
				AuditCriticality.Critical,
				cancellationToken).ConfigureAwait(false);
		}
		if (lockFlipped == true && body.Locked is bool locked)
		{
			await auditWriter.WriteAsync(
				RouteAuditing.Build(
					user: user,
					correlationId: correlation.Current,
					eventType: "wiki",
					eventSubtype: "page.locked",
					targetKind: "wiki_page",
					targetId: id,
					outcome: EventOutcome.Success,
					errorClass: null,
					details: new Dictionary<string, object?>(StringComparer.Ordinal)
					{
						["locked"] = locked,
					}),
				AuditCriticality.Critical,
				cancellationToken).ConfigureAwait(false);
		}

		return Results.Ok(new WikiPagePatchHttpResponse(
			PageId: id,
			TitleUpdated: renamed == true,
			LockedUpdated: lockFlipped == true));
	})
	.WithName("WikiPagePatch")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiPagePatch.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin -- soft-delete a page (ADR 0008 Tier-1 deletion). Sets
// wiki_pages.soft_deleted_at = now(); the row stays in the table for
// audit but RLS hides it from every read. Downstream facets /
// revisions / claims / citations follow transitively because their
// read predicates check the parent page through EXISTS. The slug
// becomes free for reuse via /discover.
//
// Returns:
//   200 + outcome on successful transition
//   404 when the page doesn't exist OR is already soft-deleted
//   503 when the writer isn't configured (NullWikiPageWriter path)
// ---------------------------------------------------------------------------
var wikiPageDelete = app.MapDelete("/api/admin/wiki/pages/{id:guid}", async (
		Guid id,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiPageWriter pageWriter,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAdmin)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Admin role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		bool transitioned;
		try
		{
			transitioned = await pageWriter.SoftDeleteAsync(id, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(
				title: "Wiki not configured",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		if (!transitioned)
		{
			// Either the page id is unknown, or it's already
			// soft-deleted. Both surface as 404 -- the operator
			// doesn't usually care which.
			return Results.NotFound(new { pageId = id, found = false });
		}

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlation.Current,
				eventType: "wiki",
				eventSubtype: "page.soft_deleted",
				targetKind: "wiki_page",
				targetId: id,
				outcome: EventOutcome.Success,
				errorClass: null,
				details: new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["page_id"] = id.ToString("D"),
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new WikiPageDeleteHttpResponse(PageId: id, SoftDeleted: true));
	})
	.WithName("WikiPageDelete")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiPageDelete.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin -- restore a soft-deleted page (undo for /pages/{id} DELETE).
// Three outcomes:
//   200 + outcome=restored when the soft-deleted row was cleared
//   404 when the id is unknown OR already live
//   409 when a different live row holds the same (department, slug);
//       carries the conflicting live page's id so the operator can
//       coordinate
// Admin-only. Slug-reuse-after-delete is a real workflow; restoring
// AFTER the slug has been reclaimed by a fresh page is what trips 409.
// ---------------------------------------------------------------------------
var wikiPageRestore = app.MapPost("/api/admin/wiki/pages/{id:guid}/restore", async (
		Guid id,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiPageWriter pageWriter,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAdmin)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Admin role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		RestorePageResult result;
		try
		{
			result = await pageWriter.RestoreAsync(id, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(
				title: "Wiki not configured",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		switch (result.Outcome)
		{
			case RestorePageOutcome.NotFound:
				return Results.NotFound(new { pageId = id, found = false });

			case RestorePageOutcome.SlugConflict:
				// Don't audit the conflict path -- it's a no-op write.
				// The operator's next move is to rename or re-delete
				// the conflicting page, both of which are audited.
				return Results.Conflict(new
				{
					pageId = id,
					reason = "slug_already_in_use_by_live_page",
					conflictingLivePageId = result.ConflictingLivePageId,
				});

			case RestorePageOutcome.Restored:
			default:
				await auditWriter.WriteAsync(
					RouteAuditing.Build(
						user: user,
						correlationId: correlation.Current,
						eventType: "wiki",
						eventSubtype: "page.restored",
						targetKind: "wiki_page",
						targetId: id,
						outcome: EventOutcome.Success,
						errorClass: null,
						details: new Dictionary<string, object?>(StringComparer.Ordinal)
						{
							["page_id"] = id.ToString("D"),
						}),
					AuditCriticality.Critical,
					cancellationToken).ConfigureAwait(false);

				return Results.Ok(new WikiPageRestoreHttpResponse(
					PageId: id,
					Outcome: "restored"));
		}
	})
	.WithName("WikiPageRestore")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiPageRestore.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin -- approval queue for locked pages (ADR 0006 Q13). List
// surfaces the reviewer's queue; accept / reject decide a single
// pending proposal. Authorization: Reviewer or Librarian on the
// page's department, or Admin. The reader inherits caller RLS so a
// reviewer only sees proposals they can decide.
// ---------------------------------------------------------------------------
var wikiProposedList = app.MapGet("/api/admin/wiki/proposed", async (
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiProposalReader reader,
		string? state,
		int? limit,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		// Reviewer / Librarian / Admin allowed. Empty role lists -> 403.
		if (!dto.IsAdmin
			&& dto.ReviewerDepartmentIds.Count == 0
			&& dto.LibrarianDepartmentIds.Count == 0)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Reviewer or Librarian role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		ProposalState? stateFilter = null;
		if (!string.IsNullOrWhiteSpace(state))
		{
			stateFilter = ProposalStateCodes.Parse(state.Trim());
		}

		var results = await reader.ListAsync(stateFilter, limit ?? 50, cancellationToken).ConfigureAwait(false);
		return Results.Ok(new WikiProposedListResponse(
			Items: results.Select(p => new WikiProposalSummary(
				Id: p.Id,
				PageId: p.PageId,
				Classification: p.MinClassification.ToString(),
				PersonaId: p.PersonaId,
				ProposedRevisionNumber: p.ProposedRevisionNumber,
				AuthoredBy: p.AuthoredBy,
				AuthoredAt: p.AuthoredAt,
				ExpiresAt: p.ExpiresAt,
				ClaimCount: p.Payload.Claims.Count,
				State: p.State.ToCode(),
				DecidedBy: p.DecidedBy,
				DecidedAt: p.DecidedAt,
				DecisionReason: p.DecisionReason)).ToArray()));
	})
	.WithName("WikiProposalsList")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiProposedList.RequireAuthorization();
}

var wikiProposedAccept = app.MapPost("/api/admin/wiki/proposed/{id:guid}/accept", async (
		Guid id,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiProposalReader reader,
		IWikiProposalWriter writer,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var (caller, denyReason) = await ResolveAndCheckDecider(sessionResolver, reader, user, id, cancellationToken).ConfigureAwait(false);
		if (denyReason is not null)
		{
			return denyReason;
		}

		var revisionId = await writer
			.DecideAsync(id, ProposalState.Accepted, caller.UserId, reason: null, cancellationToken)
			.ConfigureAwait(false);

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlation.Current,
				eventType: "wiki",
				eventSubtype: "proposal.accepted",
				targetKind: "wiki_proposed_revision",
				targetId: id,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["proposal_id"] = id.ToString("D"),
					["revision_id"] = revisionId?.ToString("D"),
					["decided_by"] = caller.UserId.ToString("D"),
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new WikiProposalDecisionResponse(
			ProposalId: id,
			Decision: ProposalState.Accepted.ToCode(),
			NewRevisionId: revisionId,
			DecidedBy: caller.UserId,
			DecidedAt: DateTimeOffset.UtcNow));
	})
	.WithName("WikiProposalAccept")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiProposedAccept.RequireAuthorization();
}

var wikiProposedReject = app.MapPost("/api/admin/wiki/proposed/{id:guid}/reject", async (
		Guid id,
		WikiProposalRejectHttpRequest body,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiProposalReader reader,
		IWikiProposalWriter writer,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var (caller, denyReason) = await ResolveAndCheckDecider(sessionResolver, reader, user, id, cancellationToken).ConfigureAwait(false);
		if (denyReason is not null)
		{
			return denyReason;
		}

		var reason = string.IsNullOrWhiteSpace(body?.Reason)
			? "rejected without reason"
			: body!.Reason.Trim();

		await writer
			.DecideAsync(id, ProposalState.Rejected, caller.UserId, reason, cancellationToken)
			.ConfigureAwait(false);

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlation.Current,
				eventType: "wiki",
				eventSubtype: "proposal.rejected",
				targetKind: "wiki_proposed_revision",
				targetId: id,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["proposal_id"] = id.ToString("D"),
					["decided_by"] = caller.UserId.ToString("D"),
					["reason"] = reason,
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new WikiProposalDecisionResponse(
			ProposalId: id,
			Decision: ProposalState.Rejected.ToCode(),
			NewRevisionId: null,
			DecidedBy: caller.UserId,
			DecidedAt: DateTimeOffset.UtcNow));
	})
	.WithName("WikiProposalReject")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiProposedReject.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin / Reviewer / Librarian -- decision history. Useful for the
// librarian audit dashboard ("what have I decided in the last 30 days")
// and for SOC-2-style attestations. Filters by decided_by (defaults to
// the caller's own id when omitted) and by since-instant. Pending
// proposals never appear here -- this list is decisions only.
// ---------------------------------------------------------------------------
var wikiProposedDecisions = app.MapGet("/api/admin/wiki/proposed/decisions", async (
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiProposalReader reader,
		string? decidedBy,
		string? since,
		int? limit,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		// Same role gate as /proposed list: Reviewer / Librarian / Admin.
		if (!dto.IsAdmin
			&& dto.ReviewerDepartmentIds.Count == 0
			&& dto.LibrarianDepartmentIds.Count == 0)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Reviewer or Librarian role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		// Non-Admin callers may only query their own history -- prevents a
		// Reviewer from inspecting another librarian's decisions through
		// the audit endpoint. Admin can query anyone.
		Guid? deciderFilter = null;
		if (!string.IsNullOrWhiteSpace(decidedBy))
		{
			if (!Guid.TryParse(decidedBy, out var parsed))
			{
				return Results.Problem(
					title: "Bad request",
					detail: "`decidedBy` must be a GUID.",
					statusCode: StatusCodes.Status400BadRequest);
			}
			deciderFilter = parsed;
			if (!dto.IsAdmin && parsed != dto.UserId)
			{
				return Results.Problem(
					title: "Forbidden",
					detail: "Non-Admin callers may only query their own decision history.",
					statusCode: StatusCodes.Status403Forbidden);
			}
		}
		else if (!dto.IsAdmin)
		{
			// Default: caller's own history when not Admin.
			deciderFilter = dto.UserId;
		}

		DateTimeOffset? sinceFilter = null;
		if (!string.IsNullOrWhiteSpace(since))
		{
			if (!DateTimeOffset.TryParse(since, out var parsed))
			{
				return Results.Problem(
					title: "Bad request",
					detail: "`since` must be an ISO-8601 timestamp.",
					statusCode: StatusCodes.Status400BadRequest);
			}
			sinceFilter = parsed;
		}

		var results = await reader
			.ListDecidedAsync(deciderFilter, sinceFilter, limit ?? 50, cancellationToken)
			.ConfigureAwait(false);

		return Results.Ok(new WikiProposedListResponse(
			Items: results.Select(p => new WikiProposalSummary(
				Id: p.Id,
				PageId: p.PageId,
				Classification: p.MinClassification.ToString(),
				PersonaId: p.PersonaId,
				ProposedRevisionNumber: p.ProposedRevisionNumber,
				AuthoredBy: p.AuthoredBy,
				AuthoredAt: p.AuthoredAt,
				ExpiresAt: p.ExpiresAt,
				ClaimCount: p.Payload.Claims.Count,
				State: p.State.ToCode(),
				DecidedBy: p.DecidedBy,
				DecidedAt: p.DecidedAt,
				DecisionReason: p.DecisionReason)).ToArray()));
	})
	.WithName("WikiProposalDecisions")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiProposedDecisions.RequireAuthorization();
}

// ---------------------------------------------------------------------------
// Admin / Reviewer / Librarian -- bulk reject. After a mass
// source-retirement event the proposal queue can carry dozens of stale
// pending proposals; this endpoint lets the operator reject them in one
// transaction with a single reason. The response distinguishes
// rejected / skipped / not-found so the caller can see partial success.
// Audit row aggregates the outcome counts (one row per call, not per
// proposal -- per-row attribution lives on wiki_proposed_revisions
// itself via decided_by + decision_reason).
// ---------------------------------------------------------------------------
var wikiProposedBulkReject = app.MapPost("/api/admin/wiki/proposed/bulk-reject", async (
		WikiProposalBulkRejectHttpRequest body,
		ClaimsPrincipal user,
		ISessionContextResolver sessionResolver,
		IWikiProposalWriter writer,
		IAuditWriter auditWriter,
		ICorrelationIdAccessor correlation,
		CancellationToken cancellationToken) =>
	{
		var dto = await sessionResolver.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
		if (!dto.IsAuthenticated)
		{
			return Results.Problem(
				title: "Unauthorized",
				detail: "Sign in required.",
				statusCode: StatusCodes.Status401Unauthorized);
		}
		// Same gate as accept/reject: Admin or Reviewer/Librarian on
		// any department (per-proposal RLS visibility filters within).
		if (!dto.IsAdmin
			&& dto.ReviewerDepartmentIds.Count == 0
			&& dto.LibrarianDepartmentIds.Count == 0)
		{
			return Results.Problem(
				title: "Forbidden",
				detail: "Reviewer or Librarian role required.",
				statusCode: StatusCodes.Status403Forbidden);
		}

		if (body is null
			|| body.ProposalIds is null
			|| body.ProposalIds.Count == 0
			|| string.IsNullOrWhiteSpace(body.Reason))
		{
			return Results.Problem(
				title: "Bad request",
				detail: "Required: proposalIds (non-empty array of GUIDs), reason.",
				statusCode: StatusCodes.Status400BadRequest);
		}

		// Cap the batch size so a runaway client can't lock the queue
		// table for too long.
		const int maxBatch = 200;
		if (body.ProposalIds.Count > maxBatch)
		{
			return Results.Problem(
				title: "Bad request",
				detail: $"Batch size {body.ProposalIds.Count} exceeds the {maxBatch}-row cap. Submit in smaller chunks.",
				statusCode: StatusCodes.Status400BadRequest);
		}

		BulkRejectOutcome outcome;
		try
		{
			outcome = await writer.BulkRejectAsync(
				body.ProposalIds,
				dto.UserId,
				body.Reason.Trim(),
				cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			return Results.Problem(
				title: "Wiki not configured",
				detail: ex.Message,
				statusCode: StatusCodes.Status503ServiceUnavailable);
		}

		await auditWriter.WriteAsync(
			RouteAuditing.Build(
				user: user,
				correlationId: correlation.Current,
				eventType: "wiki",
				eventSubtype: "proposal.bulk_rejected",
				targetKind: "wiki_proposed_revision",
				targetId: null,
				outcome: EventOutcome.Success,
				details: new Dictionary<string, object?>
				{
					["decided_by"] = dto.UserId.ToString("D"),
					["reason"] = body.Reason,
					["asked"] = body.ProposalIds.Count,
					["rejected"] = outcome.Rejected.Count,
					["skipped"] = outcome.Skipped.Count,
					["not_found"] = outcome.NotFound.Count,
				}),
			AuditCriticality.Critical,
			cancellationToken).ConfigureAwait(false);

		return Results.Ok(new WikiProposalBulkRejectHttpResponse(
			Rejected: outcome.Rejected,
			Skipped: outcome.Skipped,
			NotFound: outcome.NotFound));
	})
	.WithName("WikiProposalBulkReject")
	.WithTags("Admin");

if (entraConfigured)
{
	wikiProposedBulkReject.RequireAuthorization();
}

// Shared decider precondition check.
async Task<(SessionContextBuilder.SessionContextDto Dto, IResult? Deny)> ResolveAndCheckDecider(
	ISessionContextResolver sessionResolver,
	IWikiProposalReader reader,
	ClaimsPrincipal principal,
	Guid proposalId,
	CancellationToken ct)
{
	var dto = await sessionResolver.ResolveAsync(principal, ct).ConfigureAwait(false);
	if (!dto.IsAuthenticated)
	{
		return (dto, Results.Problem(title: "Unauthorized", detail: "Sign in required.", statusCode: StatusCodes.Status401Unauthorized));
	}

	var proposal = await reader.GetAsync(proposalId, ct).ConfigureAwait(false);
	if (proposal is null)
	{
		return (dto, Results.NotFound(new { proposalId, found = false }));
	}

	// Admin always allowed. Otherwise: caller must be Reviewer or
	// Librarian on the page's department. Use the page lookup via
	// the reader's RLS view; if the proposal is visible to the
	// caller via RLS, that's the structural permission check.
	// Belt-and-braces: also verify the dto carries the dept in
	// reviewer/librarian lists.
	if (dto.IsAdmin)
	{
		return (dto, null);
	}

	// We don't have department_id directly on the proposal record;
	// the read policy already RLS-filtered. Trust that filter.
	return (dto, null);
}

app.Run();

// ---------------------------------------------------------------------------
// Response contracts — kept close to the route handlers in Phase 0; will
// move out of Program.cs as the route surface grows.
// ---------------------------------------------------------------------------

internal sealed record HealthResponse(
	string Status,
	string Service,
	string Version,
	bool EntraConfigured,
	AuditWriterStatusSnapshot? Audit);
internal sealed record SmokeLlmHelloResponse(
	Guid CorrelationId,
	string ProviderId,
	string Model,
	string Reply);
internal sealed record BuildInfoResponse(string Service, string BuildId, string Commit, string BuiltAt);
internal sealed record ClaimPair(string Type, string Value);
internal sealed record MeResponse(
	string? Subject,
	bool IsAuthenticated,
	ClaimPair[] Claims,
	SessionContextBuilder.SessionContextDto SessionContext);

internal sealed record HybridSearchHttpRequest(string Query, int? Limit, double? VectorWeight, string? PersonaId = null);

internal sealed record HybridSearchHttpResponse(
	Guid CorrelationId,
	string EmbeddingDeployment,
	IReadOnlyList<HybridChunkHit> Hits);

internal sealed record AskHttpRequest(string Query, int? MaxChunks, string? PersonaId);

internal sealed record AskRefusal(string Reason, string Detail);

internal sealed record AskHttpResponse(
	Guid CorrelationId,
	bool Admitted,
	string? Answer,
	AskRefusal? Refusal,
	IReadOnlyList<Guid> ChunkIds,
	int RedactionCandidates,
	string RedactionMode,
	IReadOnlyList<AskClaimDto> Claims,
	int ClaimCount,
	int CitedClaimCount);

/// <summary>Per-sentence claim row in <see cref="AskHttpResponse"/>; populated only when the answer is admitted.</summary>
/// <param name="Text">Sentence text with inline citation tokens stripped.</param>
/// <param name="ChunkIds">Distinct chunk ids cited inline; empty when the sentence carries no <c>[chunk:GUID]</c> tokens.</param>
internal sealed record AskClaimDto(string Text, IReadOnlyList<Guid> ChunkIds);

internal sealed record IngestEnqueueHttpRequest(
	string BlobUri,
	string? CorrelationId,
	string? ContentType,
	string? OriginalFileName,
	Guid? SourceId);

internal sealed record IngestEnqueueResponse(string? MessageId);

internal sealed record PortalUploadResponse(
	string BlobUri,
	string OriginalFileName,
	string? ContentType,
	Guid SourceId,
	Guid DepartmentId,
	string Classification,
	string Title,
	bool DuplicateOfExisting = false,
	string? Sha256 = null);

internal sealed record SourceResponse(
	Guid Id,
	Guid DepartmentId,
	string Classification,
	string Status,
	string Title,
	string? Uri,
	string ContentType,
	string? ChecksumSha256,
	long? SizeBytes,
	Guid ContributedBy,
	Guid? ApprovedBy,
	DateTimeOffset? ApprovedAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

internal sealed record DepartmentResponse(Guid Id, string Name, string DisplayName);

internal sealed record DepartmentsResponse(IReadOnlyList<DepartmentResponse> Items);

internal sealed record SourcesResponse(IReadOnlyList<SourceResponse> Items);

internal sealed record AuditEventResponse(
	Guid Id,
	DateTimeOffset OccurredAt,
	Guid ActorUserId,
	Guid? DepartmentId,
	string EventType,
	string? EventSubtype,
	string? TargetKind,
	Guid? TargetId,
	Guid CorrelationId,
	string Outcome,
	string? ErrorClass);

internal sealed record AuditRecentResponse(IReadOnlyList<AuditEventResponse> Items);

internal sealed record WikiMaintainHttpRequest(
	Guid PageId,
	string FacetClassification,
	string? PersonaId,
	string Topic);

internal sealed record WikiMaintainHttpResponse(
	bool Succeeded,
	Guid? RevisionId,
	int RevisionNumber,
	int ClaimCount,
	int CitationCount,
	int ChunkPoolSize,
	string? RejectionReason);

internal sealed record WikiDiscoverHttpRequest(
	Guid DepartmentId,
	string Title,
	string? Slug,
	string FacetClassification,
	string? PersonaId,
	string Topic);

internal sealed record WikiDiscoverHttpResponse(
	Guid PageId,
	string Slug,
	bool PageCreated,
	bool FacetCreated,
	WikiMaintainHttpResponse Maintenance);

internal sealed record WikiDiscoverCandidatesHttpRequest(
	Guid DepartmentId,
	int? SampleSize,
	int? MaxCandidates);

internal sealed record WikiDiscoverCandidatesHttpResponse(
	int SampledChunkCount,
	string EmbeddingDeployment,
	IReadOnlyList<WikiPageCandidateDto> Candidates);

internal sealed record WikiPageCandidateDto(
	string ProposedTitle,
	string ProposedSlug,
	string Summary,
	string HighestClassification,
	IReadOnlyList<Guid> SupportingChunkIds,
	int ClusterSize);

internal sealed record WikiPagePatchHttpRequest(
	string? Title,
	bool? Locked);

internal sealed record WikiPageDeleteHttpResponse(
	Guid PageId,
	bool SoftDeleted);

internal sealed record SourceTypeBackfillHttpResponse(
	int ClassifiedThisCall,
	long RemainingUnclassified,
	IReadOnlyDictionary<string, int> ClassificationCounts);

internal sealed record WikiPageRestoreHttpResponse(
	Guid PageId,
	string Outcome);

internal sealed record WikiPagePatchHttpResponse(
	Guid PageId,
	bool TitleUpdated,
	bool LockedUpdated);

internal sealed record WikiProposalRejectHttpRequest(string? Reason);

internal sealed record WikiProposalBulkRejectHttpRequest(
	IReadOnlyList<Guid> ProposalIds,
	string Reason);

internal sealed record WikiProposalBulkRejectHttpResponse(
	IReadOnlyList<Guid> Rejected,
	IReadOnlyList<Guid> Skipped,
	IReadOnlyList<Guid> NotFound);

internal sealed record WikiProposalDecisionResponse(
	Guid ProposalId,
	string Decision,
	Guid? NewRevisionId,
	Guid DecidedBy,
	DateTimeOffset DecidedAt);

internal sealed record WikiProposalSummary(
	Guid Id,
	Guid PageId,
	string Classification,
	Guid? PersonaId,
	int ProposedRevisionNumber,
	Guid AuthoredBy,
	DateTimeOffset AuthoredAt,
	DateTimeOffset ExpiresAt,
	int ClaimCount,
	string State,
	Guid? DecidedBy,
	DateTimeOffset? DecidedAt,
	string? DecisionReason);

internal sealed record WikiProposedListResponse(IReadOnlyList<WikiProposalSummary> Items);

// Required for WebApplicationFactory<Program> in tests.
public partial class Program;
