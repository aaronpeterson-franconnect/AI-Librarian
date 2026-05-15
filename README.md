# AI Librarian

> A centralized, enterprise-grade knowledge platform where every department's
> knowledge becomes a living, LLM-maintained wiki — searchable from any AI
> client (Cursor, Copilot, Claude, Teams, ChatGPT Enterprise) through a single
> Model Context Protocol (MCP) endpoint, with classification-driven access
> control, full audit trail, and verifiable citations on every claim.

## What this is

AI Librarian is the company's **single source of synthesized knowledge**.
Every department curates its own corpus — documents, code, SQL, architecture
diagrams, meeting recordings, runbooks, transcriptions. The system distills
that corpus into an interlinked wiki that the LLM maintains automatically,
and exposes the whole thing through one MCP endpoint that respects each
caller's identity and authorized departments.

It's inspired by two reference projects:

- [**Open Brain (OB1)**](https://github.com/NateBJones-Projects/OB1) — the
  runtime pattern: one database, one AI gateway, one MCP endpoint, with
  Postgres Row-Level Security for multi-tenant isolation.
- [**Karpathy's LLM Wiki pattern**](https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f)
  — the content discipline: raw sources, an LLM-maintained wiki, a schema
  that governs how the wiki is built and kept current.

We layer on what neither alone provides: enterprise-grade auditability,
classification-driven read access combined with department + role-based
write authority via Microsoft Entra ID, an LLM-agnostic gateway operating
exclusively against enterprise-tier providers with no-training data-handling
guarantees, multimodal ingest (video / audio / images / code / SQL),
claim-level citation contracts, a tiered deletion model that satisfies
right-to-be-forgotten without breaking the audit trail, and a
**persona-aware decision-support layer** that tunes retrieval, synthesis,
and a curated set of internal autonomous actions to the user's current
work-context (Engineering, Product, SRE, Sales, Marketing, Customer
Success, Legal, HR).

## Status

**Phase 0 — Foundations** is **closed from an engineering baseline**: the
architecture is fully documented (sixteen ADRs, persona model, decision-support
roadmap), the .NET 9 solution is scaffolded, Semantic Kernel–based LLM gateway
implementations (Azure OpenAI when configured) with ADR 0012 startup
diagnostics are registered from the API, Postgres RLS session-pushdown is in
place, and the API ships a `/health` endpoint with Entra ID auth wired in.
Liquibase migrations and Azure Bicep (`deploy/bicep/main.bicep`) deploy the data
plane, observability, Service Bus, Key Vault, and Container Apps environment;
an **optional AiLibrarian.Api Container App** is included when you pass
`deployApiContainerApp` and `apiContainerImage` (see [`deploy/README.md`](deploy/README.md)).
Use `POST /api/smoke/llm/hello` (with Entra when configured) to verify an
audited LLM round-trip once Azure OpenAI is enabled. Remaining **operator**
work is a real Azure subscription deploy, Entra app registrations, image build
push, and wiring secrets / connection strings for Postgres and the LLM
provider. See [`deploy/README.md`](deploy/README.md) for the Phase 0 smoke
checklist and [`docs/llm-providers.md`](docs/llm-providers.md) for provider
configuration.

See [`docs/phasing.md`](docs/phasing.md) for the full Phase 0 → Phase 5
plan.

For the lowest-cost Azure pilot, use the slim data-plane template
[`deploy/bicep/main-pilot.bicep`](deploy/bicep/main-pilot.bicep) and the
step-by-step runbook
[`deploy/runbooks/pilot-minimal-azure.md`](deploy/runbooks/pilot-minimal-azure.md).

## Repository layout

| Folder | Purpose |
|---|---|
| [`docs/`](docs/) | Architecture, ADRs, persona briefs, phasing, glossary, stakeholder briefs |
| [`src/`](src/) | .NET 9 solution — domain, auditing, LLM gateway, infrastructure, API, **Blazor portal** (`AiLibrarian.Portal`), **stdio MCP host** (`AiLibrarian.Mcp`), **CLI** (`ailib`), **ingest worker** (`AiLibrarian.IngestWorker`), **Skills** (`AiLibrarian.Skills.Markdown`, …) |
| [`tests/`](tests/) | xUnit test projects — one per source project |
| [`db/changelog/`](db/changelog/) | Liquibase changeSets — Postgres schema, RLS policies, partitioned audit ledger |
| [`deploy/bicep/`](deploy/bicep/) | Azure infrastructure-as-code (Container Apps, PostgreSQL Flexible Server, Blob, Service Bus, Key Vault) |

## Building locally

Requirements: .NET 9 SDK, Azure CLI, git. Liquibase and Bicep are needed
only when running migrations or deploying infrastructure.

```sh
dotnet restore AiLibrarian.sln
dotnet build AiLibrarian.sln
dotnet test  AiLibrarian.sln
```

The **portal** (Phase 1 upload UI) runs with `dotnet run --project src/AiLibrarian.Portal`
alongside the API on `http://localhost:5071` by default; set **`Api:BaseUrl`** in the
portal `appsettings` if your API port differs.

The MCP server speaks stdio (for Cursor / Claude Desktop). For a quick dev loop use
`dotnet run --project src/AiLibrarian.Mcp`. Point it at a running API by setting
**`Api:BaseUrl`** in `src/AiLibrarian.Mcp/appsettings.json` (or environment variable
**`Api__BaseUrl`**). For the Phase 1 workstation flow, build the CLI
(`dotnet build src/AiLibrarian.Cli`) and run `ailib login` then `ailib mcp` so the
MCP process receives **`AILIB_ACCESS_TOKEN`**; you can also set **`AILIB_API_BASE_URL`**
(or **`Cli:Api:BaseUrl`** in the CLI `appsettings.json`) so the CLI forwards the API
URL into the MCP child process. When Entra is enabled on the API, the same bearer
token is sent on hybrid search and ingest enqueue requests. Ingest enqueue returns
503 until **`IngestQueue:ConnectionString`** is configured on the API. Details in
[`src/README.md`](src/README.md).

The API runs without Entra configuration in `Development` (the
`/health` endpoint stays anonymous, `/me` returns 401 until you
configure `AzureAd:TenantId` and `AzureAd:ClientId` in
`appsettings.Development.json` or User Secrets).

## Where to start reading

Pick by audience:

- **Leadership**: [`docs/executive-summary.md`](docs/executive-summary.md)
  — one-page brief covering the opportunity, value, cost, timeline,
  risks, and what's needed from leadership.
- **Architects / engineers**: [`docs/architecture.md`](docs/architecture.md)
  — the full system blueprint.
- **Project planning**: [`docs/phasing.md`](docs/phasing.md) — six-phase
  rollout plan, Phase 0 through Phase 5.
- **Newcomers**: [`docs/glossary.md`](docs/glossary.md) — terms used
  throughout (Source, Claim, Skill, Librarian, Directive, etc.).
- **Reviewers**: [`docs/adr/`](docs/adr/) — sixteen architecture
  decision records, each with context, decision, consequences,
  alternatives.
- **LLM providers (ADR 0012)**: [`docs/llm-providers.md`](docs/llm-providers.md)
  — approved server-side gateway targets and approved client-side AI tools.
- **Personas**: [`docs/personas.md`](docs/personas.md) — the
  eight defined work-context personas, the four-dimension model
  (department, role, classification, persona), and the v1 pilot
  scope.
- **Decision-support roadmap**: [`docs/decision-support-roadmap.md`](docs/decision-support-roadmap.md)
  — per-persona rollout plan and recommend → shadow → autonomous
  progression for each persona's internal autonomous-action set.
- **Stakeholder briefs**: [`docs/stakeholder-briefs/`](docs/stakeholder-briefs/)
  — one-page briefs drafted for non-engineering reviewers (Legal,
  IT, Operations, Finance) summarizing what we propose and what
  we need from them.
- **Tracking TBDs**: [`docs/open-questions.md`](docs/open-questions.md)
  — known open decisions with owners and target phases.
- **Future enhancements**: [`docs/future-enhancements.md`](docs/future-enhancements.md)
  — deliberately-deferred capabilities the v1 architecture is
  designed to receive when a real trigger arrives.

## Architecture decision records

| # | Decision |
|---|---|
| [0001](docs/adr/0001-data-platform-postgres-pgvector.md) | Use Azure Database for PostgreSQL Flexible Server with `pgvector` as the single data spine |
| [0002](docs/adr/0002-stack-dotnet-azure.md) | Build on .NET 9 + ASP.NET Core, hosted on Azure Container Apps |
| [0003](docs/adr/0003-llm-gateway-semantic-kernel.md) | Microsoft Semantic Kernel as the LLM-agnostic gateway |
| [0004](docs/adr/0004-mcp-as-single-access-layer.md) | All AI client access goes through one MCP server |
| [0005](docs/adr/0005-rls-with-entra.md) | Flat departments, role-based access via Entra groups, enforced with Postgres RLS |
| [0006](docs/adr/0006-llm-only-wiki-with-directives.md) | The wiki is LLM-authored only; librarians govern via sources, policies, directives, and page locks |
| [0007](docs/adr/0007-claim-level-citation-contract.md) | Every wiki claim must cite a real, traceable source by ID and span anchor |
| [0008](docs/adr/0008-tiered-deletion-and-rtbf.md) | Three deletion tiers (soft / hard / quarantine) with audit-event preservation |
| [0009](docs/adr/0009-skill-plugin-pattern.md) | File-format support is delivered as self-contained Skill plugins |
| [0010](docs/adr/0010-audit-ledger.md) | Single append-only audit ledger in Postgres, partitioned monthly, exported to SIEM |
| [0011](docs/adr/0011-data-classification.md) | Data classification (Public / Internal / Confidential / Restricted) is the default access boundary; explicit `source_shares` for cross-department exceptions |
| [0012](docs/adr/0012-enterprise-tier-llm-access.md) | All LLM access (server-side gateway and client-side MCP clients) requires enterprise-tier provider agreements with no-training and bounded-retention data-handling guarantees |
| [0013](docs/adr/0013-hyperscaler-deployment-scope.md) | Deployment scope is hyperscaler-only — Azure (primary) or AWS (alternate); on-prem, air-gapped, and sovereign-private-cloud are permanently out of scope |
| [0014](docs/adr/0014-personas-first-class.md) | Personas are a first-class organizing concept (fourth dimension alongside department, role, classification); they shape retrieval, synthesis, and autonomous-action authority — not visibility |
| [0015](docs/adr/0015-persona-aware-retrieval-synthesis.md) | Persona context tunes the existing retrieval and synthesis pipeline (one pipeline, parameterized) and adds a persona dimension to wiki page facets |
| [0016](docs/adr/0016-persona-internal-autonomous-actions.md) | Internal autonomous actions are scoped per persona, progress recommend → shadow → autonomous, are reversible by design, and **never** include autonomous customer-facing actions or AI-direct money/refund decisions |

## Personas

AI Librarian is organized around four dimensions: **department**,
**role**, **classification**, and **persona**. Persona is the
work-context the user is in: Engineering triage looks different
from Product synthesis, which looks different from Sales prep. The
v1 roster has eight personas with the v1 pilot wiring the
**Engineering persona** end-to-end. The other seven (Product, SRE,
Sales, Marketing, Customer Success, Legal, HR) are defined and
schema-scaffolded; full wiring lands in v2+.

Two carve-outs are **permanent** in the persona model: no
autonomous customer-facing actions, and no AI-direct money/refund
decisions. The AI may analyze, recommend, and draft on those
matters; humans always make the binding call.

See [`docs/personas.md`](docs/personas.md),
[`docs/personas/`](docs/personas/), and
[`docs/decision-support-roadmap.md`](docs/decision-support-roadmap.md).

## Deployment scope

AI Librarian is a **cloud-only** workload. Supported deployment targets
are **Microsoft Azure** (primary) and **Amazon Web Services** (alternate).
On-premises, air-gapped, and customer-datacenter / sovereign-private-cloud
deployments are explicitly out of scope — see
[ADR 0013](docs/adr/0013-hyperscaler-deployment-scope.md) for the
guardrail and the conditions that would re-open the decision.

## License

TBD — pending Legal review. Internal use only until then.
