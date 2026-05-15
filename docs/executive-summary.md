# AI Librarian — Executive Summary

> One-page brief for leadership. Architecture detail lives in
> [`architecture.md`](architecture.md) and the [ADRs](adr/).

## The opportunity

Our institutional knowledge is fragmented across people, file shares,
Teams chats, code repositories, recorded meetings, and tribal memory.
When an employee leaves or changes roles, that context leaves with
them. AI tools rediscover the same context on every conversation.
Onboarding is slow. Decisions get re-litigated. The cost of "knowing
what we already know" grows every quarter.

The AI agent ecosystem has, in the past year, standardized on a
single protocol (Model Context Protocol) for connecting AI tools to
internal data. We can build the platform once and have it work across
every AI tool the company uses — Copilot, Cursor, Claude, ChatGPT
Enterprise, Teams — instead of a new integration for every tool.

## What we're building

**AI Librarian** is a centralized, departmentally-curated knowledge
platform. Each department's "Librarian" approves what enters their
corpus; an LLM continuously distills the corpus into a living wiki;
anyone in the company can ask questions through any AI tool and get
answers with verifiable citations back to the original source. The
system is multimodal from day 1 — text, code, SQL, recorded meetings,
diagrams, all ingestable and citable.

## Business value

- **Faster onboarding.** A new hire's day-1 questions get answered by
  the institutional record, not by interrupting their team.
- **Reduced knowledge loss** when people change roles or leave.
- **AI productivity multiplier.** Every AI tool the company already
  pays for becomes context-aware about *our* business, not just
  general training data.
- **Cross-department collaboration as the default**, not the
  exception: Engineering can read Marketing's positioning; a new
  hire in Sales can read Engineering's release process; Operations
  can consult Finance's vendor-management policy — all without
  permission requests. Sensitive material (`Confidential` /
  `Restricted`) stays strictly scoped, with explicit auditable
  shares for one-off exceptions.
- **Compliance posture.** Every answer cites a real source; full
  audit trail; right-to-be-forgotten and tiered deletion designed in
  from day 1; SOC2/SOX-grade governance.
- **Vendor-neutrality.** LLM-agnostic at the gateway; we can
  hot-swap providers as the market evolves.

## Cost and timeline

- **12–17 weeks to v1** with 2–4 engineers, in six phases. Each
  phase ends in a working, demonstrable deliverable, not a checkpoint.
- **Pilot department: Engineering.** Diverse content (code, SQL,
  runbooks, recordings) and a tolerant audience for v1 rough edges.
- **Pure Azure stack.** Container Apps, PostgreSQL Flexible Server,
  Blob Storage, Service Bus, Key Vault, Entra ID, Azure OpenAI,
  Azure AI Speech / Vision / Document Intelligence. No SaaS
  dependencies for sensitive data; everything in our tenant.
- **Pure .NET 9 codebase.** No Python, no Node — leverages existing
  team competence and operational practice.
- **Recurring cost** dominated by LLM tokens (Azure OpenAI),
  embedding cost, and storage. Material but bounded; per-department
  budget caps and tier-based retention prevent runaway.

## What we're committing to architecturally

| Dimension | Commitment |
|---|---|
| Identity | Microsoft Entra ID, role-based access via standard groups |
| Read access | Classification-driven: `Internal` content is readable across the company by default; `Confidential` and `Restricted` content stays department-scoped. Cross-department collaboration on routine knowledge is the default, not the exception. |
| Write authority | Strictly department + role gated. Every department owns who can submit, approve, govern, and delete its sources, regardless of who can read them. |
| Access control | Postgres Row-Level Security enforces both axes — leakage is structurally impossible at the database layer. |
| Auditability | Append-only ledger of every action; SIEM export to Sentinel |
| Citations | Every LLM answer cites a real source; structural validator |
| Deletion | Three tiers (soft / hard / quarantine); RTBF-ready cascade |
| Classification | Four-tier ladder (`Public` / `Internal` / `Confidential` / `Restricted`) — the default access boundary, with explicit per-source `source_shares` for cross-department exceptions |
| Wiki page facets | Pages with mixed-classification source pools produce a per-tier facet (Internal facet for company-wide, Confidential facet for department-only) so cross-department reading never leaks sensitive material |
| LLM-neutrality | Single gateway; provider hot-swap by configuration |
| LLM data handling | Every LLM endpoint (server-side providers and client-side AI tools) is required to be on an enterprise-tier agreement with no-training, bounded-retention guarantees. Audit metadata-only by default; sensitive content is contractually protected from provider retention. |
| AI client neutrality | Single MCP server; works with Cursor (Business), Copilot (Enterprise), Claude (Work/Enterprise), ChatGPT (Enterprise), Teams (M365 Enterprise) |
| Multimodal | Day-1 support for code, SQL, PDF, Office, audio, video, images |
| Deployment scope | Cloud-only. **Microsoft Azure (primary) or Amazon Web Services (alternate)**. On-premises, air-gapped, and customer-datacenter deployments are permanently out of scope. The architecture is portable between Azure and AWS at the adapter layer; identity (Microsoft Entra ID) is unaffected by the choice. |
| Persona-driven decision support | The AI Library is a **persona-aware decision-support platform**, not just a knowledge index. Eight personas defined (Engineering, Product, SRE, Sales, Marketing, Customer Success, Legal, HR); the v1 pilot wires Engineering. Persona shapes retrieval, synthesis, and which internal autonomous actions a user may invoke — but **not** what they can read. **Permanent carve-outs**: no autonomous customer-facing actions, and no AI-direct money/refund decisions. The AI may analyze, recommend, and draft for those decisions; a human always makes the binding call. |

Sixteen Architecture Decision Records capture each commitment with
the context, the alternatives we rejected, and the consequences.

## Risks and how we manage them

- **LLM quality.** Mitigated by the structural citation contract,
  a spot-check linter (a separate model grades a sample of claims
  for support quality), and a Reader-facing "report a bad claim"
  workflow.
- **Cost overrun.** Per-department budgets, tier-based retention,
  metadata-only audit on AI queries (no full prompt/response
  capture), embedding lifecycle policy.
- **Compliance and data residency.** Azure-tenant-hosted; right-to-
  be-forgotten cascade implemented from day 1; classification labels;
  Legal sign-off required on retention specifics before Phase 0.
- **Scale.** Year-1 volumes (3–5M chunks) sit comfortably in our
  storage choice. Year-2/3 may require a vector-store migration;
  we have concrete metric triggers and a documented migration path.
- **Adoption.** Engineering pilot is intentional — tolerant
  audience and diverse content. Broader rollout follows v1 stability.

## Decision-support scope (what's wired in v1 vs. v2+)

The AI Library is designed as a **persona-aware decision-support
platform**. v1 wires the Engineering persona end-to-end — including
the first wave of *internal* autonomous actions in Recommend mode
(triage classification, ticket routing, runbook attachment). v2 and
beyond wire the other seven personas through a Recommend → Shadow →
Autonomous progression that requires real volume and measurable
agreement-with-human rates before each action graduates.

Two limits are **permanent and structural**, not phase-deferred:

- **No autonomous customer-facing actions.** Drafts intended for
  customer audiences route to internal review queues; humans
  effect any send, status change, or external commitment.
- **No AI-direct money or refund decisions.** AI may analyze,
  recommend, and draft on money/refund-adjacent work; humans always
  make the binding call.

See [`personas.md`](personas.md) for the persona roster,
[`decision-support-roadmap.md`](decision-support-roadmap.md) for
the per-persona rollout plan, and ADRs 0014-0016 for the
architectural commitments.

## What we need from leadership

1. **Architecture sign-off** on the sixteen ADRs (already documented).
2. **Headcount approval**: 2–4 engineers for 12–17 weeks (v1
   foundational rollout); the Decision-Support track adds roughly
   1.5-2 weeks to v1 timeline for persona schema scaffolding and
   the Engineering pilot wiring.
3. **Azure subscription and quotas**: Azure OpenAI, Container Apps
   environment, PostgreSQL Flexible Server, Blob Storage.
4. **Cross-functional access**:
	- **Legal**: sign-off on retention period, RTBF scope, audit
	  scrubbing rules.
	- **IT**: Entra app registrations, group-naming convention,
	  service-principal provisioning.
	- **Engineering leadership**: pilot department commitment, initial
	  librarian designation.
5. **Executive sponsor** to unblock cross-functional dependencies as
   they arise.

## Appendix — where to read more

- [`architecture.md`](architecture.md) — full system blueprint
- [`phasing.md`](phasing.md) — six-phase rollout plan with the
  Decision-Support track running parallel
- [`personas.md`](personas.md) — the eight defined personas and
  the four-dimension model (department, role, classification,
  persona)
- [`decision-support-roadmap.md`](decision-support-roadmap.md) —
  per-persona rollout plan and recommend → shadow → autonomous
  progression
- [`adr/`](adr/) — the sixteen decision records
- [`open-questions.md`](open-questions.md) — known TBDs with owners
  and target phases
