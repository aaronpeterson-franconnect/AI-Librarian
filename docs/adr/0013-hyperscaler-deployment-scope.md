# ADR 0013 — Hyperscaler-only deployment scope (Azure or AWS); on-prem out of scope

> Status: **Accepted** · Date: 2026-04-30 · Deciders: Architect

## Context

Earlier ADRs picked technologies that happen to be portable across
clouds — `pgvector` over a cloud-proprietary vector store
([ADR 0001](0001-data-platform-postgres-pgvector.md)), Semantic Kernel
over a single provider's SDK
([ADR 0003](0003-llm-gateway-semantic-kernel.md)), MCP over a custom
HTTP surface ([ADR 0004](0004-mcp-as-single-access-layer.md)),
OpenTelemetry over Application Insights-only telemetry, Liquibase over
a cloud-specific migration runner, the Skill plugin pattern over
inline AI-service calls ([ADR 0009](0009-skill-plugin-pattern.md)).
That portability was a side benefit, not an explicit scope statement.

Without an explicit boundary, the architecture is exposed in two
directions:

1. **Over-investment**: a future architect, seeing the partial
   portability already present, extends it toward full cloud-agnostic
   support — including on-prem, air-gapped, or sovereign-private-cloud
   deployments. That investment carries a steady-state operational
   cost (running Postgres HA, MinIO, RabbitMQ, Vault, observability
   ourselves; OSS-only AI service fallbacks; ADFS / Keycloak identity)
   that the business has no need for.
2. **Under-investment**: a future architect, lacking the boundary,
   takes a hard dependency on a hyperscaler-proprietary service
   (CosmosDB, DynamoDB, Azure AI Search, Kendra, App Service-only
   features, Step Functions, Cognitive Services without container
   forms) that forecloses the cross-hyperscaler portability already
   present.

This ADR sets the boundary explicitly so neither failure mode is
left to chance.

The constraint reflects the business reality: AI Librarian is, and
will remain, a cloud workload. The organization runs on cloud-managed
infrastructure today; there is no roadmap toward on-prem operation;
no customer contract requires it; and no regulatory posture demands
it that isn't already addressable through a hyperscaler's sovereign
or regulated-industry offerings (Azure Government, AWS GovCloud).

## Decision

### Scope

**AI Librarian is a cloud-only system. Supported deployment targets
are Microsoft Azure (primary) and Amazon Web Services (alternate).**

- **In scope**: Azure (primary), AWS (alternate). Either, for any
  given deployment.
- **Out of scope, permanently**: on-premises datacenters, air-gapped
  environments, sovereign-private-cloud (e.g., a customer's
  datacenter), VMware-based private clouds, Kubernetes on bare metal,
  hybrid datacenter / cloud splits where the data plane lives in a
  customer datacenter.

The "or" is exclusive in the sense that any single deployment runs
on one hyperscaler. **This ADR does not commit to multi-cloud
active-active operation.** A given AI Librarian instance picks
Azure or AWS at deploy time and stays there. The portability
commitment is about being *able* to run on either, not about
running on both simultaneously.

### Architectural guardrail

The architecture commits to remaining **portable between Azure and
AWS**. Any new platform-level dependency that becomes load-bearing
must satisfy the test:

> Does this dependency have a credible equivalent on the other
> supported hyperscaler — meaning a managed service of comparable
> maturity, with a comparable SDK, that we could actually adopt
> within a single sprint of adapter work?

If yes, the dependency is acceptable. If no, the dependency requires
an explicit ADR amendment (this one, or a successor) before adoption.

Hyperscaler-specific bindings are acceptable **within an adapter
boundary** — e.g., the Skill plugin for PDF parsing is allowed to
target Azure AI Document Intelligence on Azure deployments and
AWS Textract on AWS deployments, because the Skill plugin pattern
([ADR 0009](0009-skill-plugin-pattern.md)) is itself the abstraction
layer.

### Approved service categories

The following categories have credible Azure ↔ AWS equivalents and
are the approved building blocks. Each row names the abstraction or
SDK that lets the application stay hyperscaler-neutral:

| Category | Azure | AWS | Application boundary |
|---|---|---|---|
| Compute (containers) | Container Apps | ECS Fargate | Dockerfile + IaC |
| Managed Postgres | Postgres Flexible Server | RDS / Aurora Postgres | `Npgsql` connection string |
| Object storage (with WORM) | Blob Storage + immutability policy | S3 + Object Lock | Storage adapter interface |
| Message bus | Service Bus | SQS + SNS | Messaging adapter interface |
| Secrets management | Key Vault | Secrets Manager | Secrets adapter interface |
| Observability | App Insights / Azure Monitor | CloudWatch / X-Ray | OpenTelemetry exporter |
| SIEM export | Sentinel | Security Lake (or third-party) | Audit-export job target |
| LLM provider | Azure OpenAI | AWS Bedrock | Semantic Kernel connector |
| Speech-to-text | Azure AI Speech (cloud or container) | Amazon Transcribe | `Skills.Media` adapter |
| OCR / vision | Azure AI Vision | Amazon Rekognition / Textract | `Skills.Image` adapter |
| Document intelligence | Azure AI Document Intelligence | AWS Textract | `Skills.Pdf` adapter |
| Identity (IdP) | Microsoft Entra ID | **Microsoft Entra ID** | OIDC; Entra works from either cloud |
| Service-to-service auth | Managed Identities | IAM Roles for Tasks (ECS) | Adapter inside the SDK call |
| IaC | Bicep | AWS CDK or Terraform | (Both supported targets — see "Easier" below) |

### Disallowed dependencies (without ADR amendment)

The following are **not** acceptable as load-bearing dependencies
because they have no credible cross-hyperscaler equivalent:

- **CosmosDB** (no AWS equivalent; DynamoDB is not a substitute)
- **DynamoDB** (no Azure equivalent; CosmosDB is not a substitute)
- **Azure AI Search / AWS Kendra** as the primary retrieval index
  (each is hyperscaler-proprietary; we use `pgvector` for v1
  exactly because it's portable)
- **AWS Step Functions / Azure Logic Apps** as the orchestration
  spine (we use queue-driven workers in .NET; orchestration logic
  stays in code)
- **App Service-only features** that don't run on Container Apps
  or ECS Fargate
- **Cognitive Services without an offline-container form** when the
  Skill plugin's other-cloud adapter would lose feature parity

A new dependency in any of these categories triggers an ADR
amendment with an explicit justification and a documented portability
plan.

### Identity is hyperscaler-independent

Microsoft Entra ID is the IdP regardless of which hyperscaler hosts
the workload. Entra is a SaaS identity service; it works from Azure,
AWS, or anywhere with internet access to the Microsoft endpoints.
[ADR 0005](0005-rls-with-entra.md) is therefore unaffected by the
choice of hyperscaler. AWS Cognito is **not** introduced as an
identity layer; AWS-native service-to-service auth (IAM Roles for
Tasks) is internal-only and does not surface to users.

### What would re-open this ADR

This decision is reviewed if any of the following becomes true:

- A customer contract requires deployment into an environment that
  is neither Azure nor AWS
- A regulatory change requires data sovereignty in a jurisdiction
  served by neither hyperscaler (and not by their sovereign
  offerings — Azure Government, AWS GovCloud, Azure Operator,
  AWS regulated-industry equivalents)
- AWS support is never used in 3+ years and the cost of cross-cloud
  discipline outweighs the optionality value (in which case we'd
  amend to "Azure-only," not the other direction)

## Consequences

### Easier

- **Clear test for new dependencies.** "Does this work on the other
  hyperscaler?" replaces ad-hoc judgment about cloud lock-in.
- **Skill plugin adapters stay focused.** Each Skill needs at most
  two cloud-aware variants (Azure and AWS) plus a content-only
  variant where applicable, not three or four.
- **No OSS-only fallbacks required.** We don't need to maintain
  Tesseract, Whisper.cpp, Apache Tika, MinIO, RabbitMQ, Vault, or
  similar substitution layers as v1 deliverables. Microsoft AI
  service containers and AWS-native services cover both cloud
  deployment paths cleanly.
- **Operational simplicity.** Managed Postgres, managed object
  storage, managed message bus, managed secrets — across either
  cloud. No platform team needs to run those services in-house.
- **Identity is unaffected.** Entra works from any cloud; the RLS
  model in [ADR 0005](0005-rls-with-entra.md) doesn't fork by
  hyperscaler.
- **Audit and classification posture is unaffected.** ADR 0010 and
  ADR 0011 reference no Azure-specific or AWS-specific primitive
  beyond the storage / SIEM adapter, both already abstracted.

### Harder

- **Architects must actively avoid hyperscaler-proprietary
  services** that lack a cross-hyperscaler equivalent — even when
  they're tempting (CosmosDB's global distribution, DynamoDB's
  serverless model, Azure AI Search's tight integration with
  Microsoft AI services).
- **IaC choice has a small tax.** Either we duplicate IaC across
  Bicep (Azure) and CDK / Terraform (AWS), or we standardize on
  Terraform for both (slight loss of cloud-native ergonomics on
  Azure). Phase 0 picks the IaC posture explicitly.
- **Some best-of-breed primitives are off the table.** A workload
  that wanted Azure AI Search's tight Microsoft-AI integration, or
  Bedrock's exclusive access to certain models, will find the
  cross-hyperscaler discipline limits choices on each cloud.

### Risks

- **A hyperscaler-proprietary dependency slips in via a convenience
  SDK or a Skill plugin and becomes load-bearing before it's
  noticed.** Mitigation: every new platform-level dependency goes
  through ADR review; periodic dependency audit (Phase 4 hardening
  candidate).
- **A future customer mandates on-prem deployment.** Mitigation:
  this ADR documents the trigger conditions for revisiting; the
  conversational portability analysis (Postgres self-managed,
  MinIO, RabbitMQ, Whisper.cpp, ADFS) in chat history maps the
  estimated lift if it ever comes up.
- **Azure-or-AWS still leaves cloud-region availability concerns.**
  Active-passive within a single hyperscaler is the working
  assumption; multi-cloud active-active is *not* implied by this
  ADR. Cross-region DR posture is a separate operational decision
  (Phase 4 deliverable).

## Alternatives considered

### Cloud-agnostic, including on-prem and air-gapped

Considered in detail (see chat history of 2026-04-30 for the
full architectural walk-through). Rejected because:

- No business need; no customer contract; no regulatory mandate
- Steady-state operational burden is roughly 2-3x: running Postgres
  HA, MinIO, RabbitMQ, Vault, observability, and audit-SIEM
  ourselves
- Open-weights LLM quality regression for the Wiki Maintainer agent
  if cloud LLMs aren't available
- Significantly higher platform-engineer headcount

### Azure-only

Considered. Rejected as needlessly closing an open door. The
current architectural choices already preserve AWS portability at
near-zero marginal cost; explicitly retreating from that would
sacrifice optionality (regulatory, customer, M&A scenarios) for
no real gain. The conversational diligence on the AWS migration
estimated 6-10 weeks of focused work — a material but recoverable
contingency to keep available.

### Multi-cloud active-active

Considered. Rejected for v1. Running the same workload simultaneously
on Azure *and* AWS introduces data-replication, identity-federation,
and consistency complexity disproportionate to any v1 benefit. May
become a Phase 5+ enhancement if cross-hyperscaler resilience is
mandated; captured in
[`../future-enhancements.md`](../future-enhancements.md) when a
trigger emerges.

### Hyperscaler-plus-sovereign (Azure + Azure Government, or AWS + GovCloud)

Considered as a path to handle regulated workloads. Not rejected,
just deferred — both hyperscalers' sovereign offerings are
substantially the same architecture as their commercial
counterparts. If a regulated customer arrives, we deploy a separate
instance into the sovereign offering; no architectural amendment
required, only IaC and operational hardening.

## References

- [ADR 0001](0001-data-platform-postgres-pgvector.md) — Postgres + pgvector (chosen partly for cross-hyperscaler portability)
- [ADR 0002](0002-stack-dotnet-azure.md) — .NET 9 + Container Apps (Container Apps is Azure-specific; AWS adapter target is ECS Fargate)
- [ADR 0003](0003-llm-gateway-semantic-kernel.md) — Semantic Kernel (provider-pluggable across hyperscalers)
- [ADR 0004](0004-mcp-as-single-access-layer.md) — MCP (cloud-neutral protocol)
- [ADR 0005](0005-rls-with-entra.md) — RLS with Entra (Entra is SaaS, hyperscaler-independent)
- [ADR 0009](0009-skill-plugin-pattern.md) — Skill plugin pattern (per-cloud adapters live inside Skills)
- [ADR 0011](0011-data-classification.md) — Data classification (independent of hosting cloud)
- [ADR 0012](0012-enterprise-tier-llm-access.md) — Enterprise-tier LLM access (independent of hosting cloud; AWS Bedrock and Azure OpenAI both qualify)
- [`../future-enhancements.md`](../future-enhancements.md) — multi-cloud active-active captured here if a trigger emerges
