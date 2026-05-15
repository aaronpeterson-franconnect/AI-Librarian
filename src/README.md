# `src/` — .NET 9 source

The AI Librarian solution. Each project's role and dependencies follow.

## Solution layout

| Project | Role |
|---|---|
| `AiLibrarian.Domain` | Pure POCOs, value objects, enums (`Classification`, `Role`, `Persona`, `Department`, `UserAuthorization`, `PersonaMembership`). Skill contracts (`ISkill`, `ISkillRegistry`, `SkillResult`, `Chunk`, …) under `Skills/` per [ADR 0009](../docs/adr/0009-skill-plugin-pattern.md). Zero NuGet dependencies. |
| `AiLibrarian.Auditing` | `IAuditWriter` + `AuditEvent` shape per [ADR 0010](../docs/adr/0010-audit-ledger.md). Persistence implementations live in Infrastructure. |
| `AiLibrarian.Infrastructure` | Postgres adapters. `Rls/RlsSessionContext` + `Rls/RlsSessionPusher` push the ADR-0005 session variables onto every transaction. `Persistence/ISourceChunkPersistence` writes chunks/embeddings; **`Retrieval/IHybridChunkSearch`** runs hybrid vector + FTS rank (`Pgvector` + `search_document`). |
| `AiLibrarian.LlmGateway.Abstractions` | `IChatProvider`, `IEmbeddingProvider`, `IRerankProvider`, `ProviderTier`, `DataHandlingProfile`, `ProviderDescriptor`. Per ADR 0003 + ADR 0012. |
| `AiLibrarian.LlmGateway` | Microsoft Semantic Kernel implementation of the abstractions. Configuration in `LlmGatewayOptions`; descriptor projection in `ProviderRegistry`. |
| `AiLibrarian.Api` | ASP.NET Core minimal API host. `/health`, `/build-info`, `/me`, **`POST /api/search/hybrid`** (embed query via `IEmbeddingProvider`, Postgres hybrid rank over `source_chunks` under RLS when `ConnectionStrings:Postgres` is set). **`POST /api/ingest/enqueue`** accepts an `IngestJobMessage`-shaped body and publishes JSON to Azure Service Bus when **`IngestQueue:ConnectionString`** is set (503 otherwise). Phase 0 smoke: `/api/smoke/llm/hello`. Container image: `src/AiLibrarian.Api/Dockerfile` (build context = repo root). |
| `AiLibrarian.Mcp` | **stdio** MCP server (`ModelContextProtocol` SDK per ADR 0004). **`search`** calls **`POST /api/search/hybrid`** on the API; **`enqueue_source`** calls **`POST /api/ingest/enqueue`**. Set **`Api:BaseUrl`** in `src/AiLibrarian.Mcp/appsettings.json` (or env **`Api__BaseUrl`**) to the API root (no trailing slash). **`get_source`** / **`list_departments`** remain Phase 1 placeholders. Reads **`AILIB_ACCESS_TOKEN`** (from `ailib mcp` or your IDE env), parses Entra `oid`/`tid` claims without validating the signature (Phase 1); tool JSON wraps the API body under **`api`** plus a **`workstation`** summary. Run via `dotnet run --project src/AiLibrarian.Mcp` or `ailib mcp`. |
| `AiLibrarian.Portal` | **Blazor Web App** (interactive Server). Phase 1 **Upload** page posts multipart files to the API **`POST /api/portal/sources/upload`**. Configure **`Api:BaseUrl`** (default `http://localhost:5071/`). Run with `dotnet run --project src/AiLibrarian.Portal` (API must expose **`Portal:CorsOrigins`** in Development — default includes `http://localhost:5215`). Entra on the API requires a bearer-capable client for uploads; local dev typically runs with Entra unset. |
| `AiLibrarian.Cli` | Workstation CLI (`ailib`): Entra **device-code** sign-in, MSAL cache under `%LOCALAPPDATA%/AiLibrarian/Cli` (cross-platform equivalent), `ailib mcp` launches the MCP host with **`AILIB_ACCESS_TOKEN`** and, when set, **`Api__BaseUrl`** from **`Cli:Api:BaseUrl`** in appsettings or env **`AILIB_API_BASE_URL`**. Configure `src/AiLibrarian.Cli/appsettings.json` (or `AILIB_TENANT_ID`, `AILIB_CLIENT_ID`, `AILIB_API_SCOPES`) with a **public client** app registration and API scopes. |
| `AiLibrarian.Skills.Markdown` | First **Skill** plugin: `MarkdownSkill` implements `ISkill`; YAML frontmatter stored as `frontmatter_raw`, paragraph-grouped chunks, `SKILL.md` manifest. |
| `AiLibrarian.IngestWorker` | .NET **Worker** host: `SkillRegistry` (Markdown), optional **Azure Service Bus**, references **`AiLibrarian.LlmGateway`** for `IEmbeddingProvider` when `Processing:GenerateEmbeddings` is **true** (default **false**). `RunContentPipeline` gates blob + canonicalize + Postgres chunks; `GenerateEmbeddings` runs after chunk upsert (needs `LlmGateway` + embedding deployment, `Embeddings:ModelDeploymentName`, dimension **1536** matching Liquibase `0015`). Npgsql uses **Pgvector** (`UseVector()`). Dead-letter reasons include `EmbeddingFailed`. |

## Adding a project

1. Create with `dotnet new` and add it to the solution:
	```sh
	dotnet new classlib -n AiLibrarian.NewThing -o src/AiLibrarian.NewThing
	dotnet sln AiLibrarian.sln add src/AiLibrarian.NewThing/AiLibrarian.NewThing.csproj
	```
2. Strip the auto-generated `Class1.cs`.
3. Replace any inline `<PackageReference Version="..." />` with the un-versioned form;
   add the version (or update an existing one) in `Directory.Packages.props` at the
   solution root.
4. The first build will fail until every package referenced has a `<PackageVersion>`
   in `Directory.Packages.props` — this is intentional, central package management
   keeps the solution coherent.

## Coding conventions

Per [`.editorconfig`](../.editorconfig):

- **Tabs**, not spaces.
- File-scoped namespaces.
- Nullable reference types **on**; nullable warnings are **errors**
  (`CS8600`/`CS8602`/`CS8603` are escalated in `.editorconfig`).
- Implicit usings on; standard library usings sort first.
- Private fields prefixed with `_`.

`Directory.Build.props` enforces `TreatWarningsAsErrors=true` and `AnalysisLevel=latest-recommended`.
The current `NoWarn` list and the rationale for each suppression live in that file.

## Tests

Test projects live in `../tests/`, one per source project. Naming convention
`AiLibrarian.<SourceProject>.Tests`. Each one references the project under test
and the standard test stack (xUnit, FluentAssertions; NSubstitute or
Testcontainers where needed).

Run all tests:

```sh
dotnet test ../AiLibrarian.sln
```
