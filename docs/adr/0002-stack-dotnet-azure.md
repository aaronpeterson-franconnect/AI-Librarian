# ADR 0002 — Build on .NET 9 + ASP.NET Core, hosted on Azure Container Apps

> Status: **Accepted** · Date: 2026-04-29 · Deciders: Architect (initial proposal — to be ratified)

## Context

We need to choose the implementation stack for AI Librarian. Constraints:

- The organization is a .NET / Azure shop. Existing operational
  capability, deployment pipelines, monitoring, and team skill set
  align with Microsoft platforms.
- We need to integrate with Microsoft Entra ID, Microsoft Graph,
  Application Insights, Azure Monitor, Azure Service Bus, Azure Blob
  Storage, Azure Key Vault, and Azure AI services.
- The two reference projects (Open Brain, the LLM Wiki community
  implementations) are written in TypeScript and Python. We do not
  need to mirror their language choices to use their patterns.
- We need first-class support for the Model Context Protocol (MCP)
  to expose tools to AI clients.
- We need an LLM-agnostic gateway (see ADR 0003).
- A subset of AI tooling has its mature implementations in Python or
  native bindings (e.g., Whisper, tree-sitter). For each, we evaluate
  Azure-native and .NET-native alternatives before considering a
  cross-language path.

## Decision

The system is implemented in **.NET 9** as the primary runtime target,
matching the version already in production elsewhere in the
organization. ASP.NET Core hosts every long-running service:

- API gateway (ingestion endpoints, web portal back-end)
- MCP server
- Background workers (ingestion pipeline, librarian agents)
- Scheduled jobs (linter, group sync)

All services are deployed to **Azure Container Apps** with per-service
scaling, revision-based rollout, and Dapr opt-in if we need it later.

The system is **pure .NET — no Python, no Node.js sidecars**. Where the
AI ecosystem traditionally lives in Python, we adopt Microsoft-native
.NET-friendly services:

| Capability | Replacement for the typical Python tool |
|---|---|
| Speech-to-text | **Azure AI Speech** batch transcription (Microsoft.CognitiveServices.Speech NuGet) for direct audio/video uploads. **Microsoft Graph** for Teams meeting transcripts (consuming the VTT that Teams already produces — no re-transcription cost). |
| Code parsing | **Roslyn** for C# (first-party, superior). **`TreeSitterSharp`** for TypeScript / JavaScript / Python / Go / Rust source files. |
| Embedding | The LLM Gateway (ADR 0003); embeddings are an HTTP call, no in-process Python needed. |
| Vision / OCR | **Azure AI Vision** and **Azure AI Document Intelligence** via .NET SDKs. |

Source-code repository structure follows the `src/` / `tests/` /
`deploy/` / `db/` / `docs/` convention shared across the organization's
.NET solutions. SQL migrations are written as Liquibase changelogs per
project conventions.

## Consequences

### Easier

- Hiring, on-call, and code-review all stay within the team's existing
  competence.
- Native integration with Entra (Microsoft.Identity.Web), Graph
  (Microsoft.Graph SDK), Service Bus (Azure.Messaging.ServiceBus),
  Blob (Azure.Storage.Blobs), Key Vault (Azure.Security.KeyVault.*),
  Application Insights (OpenTelemetry + AI extensions), Azure AI
  Speech (Microsoft.CognitiveServices.Speech), Azure AI Vision and
  Document Intelligence (Azure.AI.* packages).
- Microsoft Semantic Kernel for the LLM gateway (see ADR 0003) is
  first-party .NET.
- Container Apps gives us per-service autoscale (including scale-to-zero
  for the workers), revision rollouts, and managed identities to
  Postgres / Blob / Key Vault.
- One runtime, one observability story, one deployment pipeline,
  one set of secrets, one CI/CD shape across the entire system.
- For Teams meetings specifically: consuming existing Graph
  transcripts is free of compute cost — we already paid for the
  transcription via M365 licensing.

### Harder

- We forgo the ready-made TypeScript / Python community
  implementations of OB1 and the LLM Wiki pattern. We will translate
  their patterns and primitives, not reuse their code.
- We commit to whatever .NET-native or Azure-native option exists
  for each AI capability. If a future capability has a great Python
  library and no .NET equivalent, we either build the .NET wrapper
  or revisit this ADR rather than slipping a sidecar in.
- The MCP C# SDK is newer than the TypeScript / Python ones; we
  accept some early-adopter risk and pin to stable releases.
- `TreeSitterSharp` is community-maintained, not Microsoft. We pin
  to a known-good version and have a contingency path to write a
  thin C# wrapper around the native tree-sitter library directly if
  the binding becomes unmaintained.

### Risks

- Semantic Kernel and the MCP C# SDK both move quickly. Mitigation:
  pin to known-good versions, keep the gateway abstractions thin
  enough to swap to direct HTTP calls if needed.
- A newcomer to the project has to understand Liquibase as well as
  EF Core. Mitigation: documented in `db/README.md` once Phase 0
  lands.
- Azure AI Speech cost at "every Teams meeting" volume could be
  meaningful if we *also* re-transcribed everything Teams already
  produces. Mitigation: prefer the Graph-VTT path for Teams meetings
  and reserve Azure AI Speech for direct uploads only. Cost analysis
  in Phase 3.

## Alternatives considered

### Fork Open Brain directly (TypeScript + Python)

Fastest path to a working system but conflicts with our team's stack
and operational standards. The architecture we want — .NET API,
Entra-native, Container Apps — would still require a near-rewrite.

### Polyglot (TS for MCP + Python for AI + .NET for business logic)

Three runtimes, three observability stories, three deployment
pipelines. A poor trade for marginal "best tool per layer" benefit.

### Run on Azure Kubernetes Service (AKS)

More flexibility, but Container Apps gives us 90% of what we need with
significantly lower operational overhead. We can graduate to AKS in
Phase 5+ if scale or networking complexity justifies it.

## References

- ADR 0003 — LLM gateway (Semantic Kernel)
- ADR 0004 — MCP as single access layer
- ADR 0009 — Skill plugin pattern
- ADR 0013 — Hyperscaler deployment scope (this ADR's hosting choice — Container Apps — is Azure-specific; the AWS adapter target is ECS Fargate. Either is acceptable per ADR 0013.)
- [Azure Container Apps overview](https://learn.microsoft.com/en-us/azure/container-apps/overview)
- [.NET release lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core)
