# AI Librarian — Phasing

> Status: **Draft for review** · Companion to [`architecture.md`](architecture.md)

The phasing below is sized for a small team (2–4 engineers) and assumes
no parallel pre-work. Each phase ends with a working, deployed
deliverable that we can demo, not just a checkpoint. Phases overlap in
practice; the durations are wall-clock estimates.

## Phase 0 — Foundations (~3 weeks)

**Goal**: a deployed, secure, observable .NET solution on Azure with the
data spine, identity, and LLM gateway ready for content.

Deliverables:

- `src/` solution scaffold (per [`architecture.md`](architecture.md))
- `deploy/bicep/` for the full Azure footprint:
	- Container Apps environment
	- Optional **AiLibrarian.Api** Container App (`deployApiContainerApp` +
	  `apiContainerImage` on `main.bicep`; Dockerfile under `src/AiLibrarian.Api/`)
	- Azure Database for PostgreSQL Flexible Server + `pgvector` extension
	- Azure Blob Storage with immutability policies
	- Azure Service Bus namespace
	- Key Vault, Application Insights, Log Analytics
	- Entra app registrations (API + portal + MCP)
- Liquibase changelog skeleton in `db/changelog/`:
	- `users`, `departments`, `user_authorizations` (the `role`
	  column is a CHECK-constrained text, not a separate `roles`
	  table — per ADR 0005)
	- `sources` with `classification` column and the four-tier
	  CHECK constraint (`Public` / `Internal` / `Confidential` /
	  `Restricted`)
	- `source_shares` for explicit cross-department read grants
	- `audit_events` (partitioned by month)
	- **Persona schema scaffolding** (per
	  [ADR 0014](adr/0014-personas-first-class.md),
	  [ADR 0016](adr/0016-persona-internal-autonomous-actions.md)):
	  `personas`, `persona_memberships`,
	  `persona_action_records`, `persona_action_outcomes`. The
	  v1 persona roster is seeded with the eight personas from
	  the persona briefs in `docs/personas/`; only the
	  Engineering persona is *wired* in v1 (see Decision-Support
	  track below); other personas exist as schema rows for v2+
	  use.
	- RLS scaffolding: classification-aware read predicates on
	  `sources` (using `app.is_employee` to distinguish employees
	  from B2B guests), role-based write predicates, and grant
	  predicates on `source_shares` (covered by the integration
	  test battery). RLS on `persona_action_records` and
	  `persona_action_outcomes` mirrors the target row's
	  visibility (action records on a Confidential ticket are
	  themselves Confidential).
- `AiLibrarian.LlmGateway` with Semantic Kernel:
	- `IChatProvider`, `IEmbeddingProvider`, `IRerankProvider`
	- Azure OpenAI connector wired up; Anthropic and Ollama wired but
	  disabled by config (proves the abstraction works)
	- Per-call telemetry → audit ledger
	- Provider tier-metadata configuration (`Tier` + `DataHandling`
	  fields per provider); startup audit event listing configured
	  providers; warning-level audit event for any provider missing
	  tier metadata (per [ADR 0012](adr/0012-enterprise-tier-llm-access.md))
- `docs/llm-providers.md` — living approved-providers and approved-
  AI-clients list, owned by Security + Ops (per ADR 0012). Initial
  v0 of the doc with whatever's actually configured at Phase 0;
  grows over time as providers are evaluated and added.
- `AiLibrarian.Api` with one health endpoint and Entra auth working
  end-to-end; Postgres session variable pushdown (`app.user_id`,
  `app.is_authenticated`, `app.department_ids`, role lists,
  `app.is_admin`) demonstrated in tests including read-across-
  department scenarios for `Internal` content

Done when: a developer can clone the repo, run `bicep what-if` against a
dev sub, deploy with one command, sign in via Entra, see an audited
"hello world" round-trip through the LLM gateway (validated via
`POST /api/smoke/llm/hello` with Azure OpenAI configured — see
[`deploy/README.md`](../deploy/README.md) Phase 0 smoke checklist).

## Phase 1 — Single-department MVP (~4 weeks)

**Goal**: Engineering can upload a document and an AI client can find it.

Deliverables:

- Web portal (Blazor Server) with sign-in and a single-department upload
  page
- `Skills.Markdown`, `Skills.Pdf`, `Skills.Office` plugins
- Ingestion pipeline through Service Bus → ingestion worker
- Chunking + embedding + persistence
- Hybrid search endpoint (vector + full-text)
- `AiLibrarian.Mcp` server with `search`, `get_source`, `list_departments`
- `AiLibrarian.Cli` cross-platform binary: Entra device-code auth,
  token caching in OS-native secure storage, localhost MCP stdio
  bridge for workstation AI clients
- One Engineering department populated with seed sources
- Cursor and Claude Desktop verified as MCP clients via the CLI

Done when: an engineer uploads a PDF runbook through the portal,
30 seconds later asks Cursor "How do I rotate the staging secrets?",
and gets an accurate answer with a citation back to the runbook.

## Phase 2 — Wiki layer (~4 weeks)

**Goal**: the wiki starts compounding.

Deliverables:

- Wiki tables: `wiki_pages`, `page_facets` (with the optional
  `persona_id` dimension per
  [ADR 0006](adr/0006-llm-only-wiki-with-directives.md)
  amendment), `wiki_page_revisions` (per facet, persona-aware),
  `wiki_claims`, `wiki_claim_citations` with the immutability +
  revision contract
- Per-facet RLS policies on `page_facets` mirroring the
  classification-aware read rules used on `sources`
- Wiki Maintainer agent (Container Apps Job triggered by Service Bus)
  with two-pass synthesis-then-extraction; produces one facet per
  visibility tier when the source pool supports it (per
  [ADR 0011](adr/0011-data-classification.md))
- Citation validator: every claim must have at least one citation
  with confidence >= 0.7; writes are rejected if validation fails
- Spot-check linter: a separate, cheaper model grades a configurable
  percentage of newly-written claims against their cited chunks for
  support quality (false-but-cited detection)
- `wiki_directives` table and the librarian portal flows for editing
  policies and directives
- Approval queue: Reviewer / Librarian dashboard showing pending
  sources with the LLM's recommendation
- Page lock support (Reviewer or Librarian can lock / unlock)
- `wiki_claim_reports` table + Reader-facing "report a bad claim"
  affordance + triage workflow in the Reviewer / Librarian dashboard
- MCP tools: `get_page`, `get_neighborhood`, `ask`, `cite`,
  `list_recent_changes`

Done when: an engineer uploads three runbooks, the wiki has produced a
"Secrets Rotation" entity page that synthesizes across them, claims are
all cited, and asking Cursor "tell me everything we know about secret
rotation" returns a wiki-grounded answer.

## Phase 3 — Multimodal (~3 weeks)

**Goal**: every file type the user listed can be ingested and cited.

Deliverables:

- `Skills.Code` (tree-sitter): C#, TypeScript, Python, Go
- `Skills.Sql` (Liquibase-aware per project conventions)
- `Skills.Image` (Azure AI Vision)
- `Skills.Media` — Azure AI Speech for direct uploads, Microsoft
  Graph for Teams meeting transcripts, shared VTT parser, timestamp-
  anchored citations
- Citation rendering in the portal that handles image regions and
  audio/video timestamps

Done when: an engineer uploads a 30-minute architecture-review
recording, asks "what did we decide about the cache eviction policy?",
and gets an answer with a citation that deep-links to the exact moment
in the video.

## Phase 4 — Multi-department + RTBF (~3 weeks)

**Goal**: production-ready multi-tenancy and deletion.

Deliverables:

- Multiple flat departments stood up (Engineering, plus 2-3 of
  Finance / Legal / HR / Operations as the next pilots)
- Entra group sync job + nightly reconciliation across all
  `(department, role)` groups
- All Postgres tables under hardened RLS policies; tested with
  simulated multi-user fixtures covering: users with multiple group
  memberships, cross-department `Internal` reads (must succeed),
  cross-department `Confidential` reads (must fail without a
  `source_shares` grant), `Restricted` reads (must require
  Librarian+ in the owning department), share-grant lifecycle
  (grant, expire, revoke), and write-authority isolation regardless
  of read access
- Tiered deletion (soft / hard / quarantine) with a Cascade-Regeneration
  Worker
- Concept-level RTBF tool in the portal (Legal-initiated, librarian-
  reviewed)
- Linter on a nightly schedule
- Audit-event tombstoning rules for RTBF cases
- **Vector-store migration runbook + dual-write scripts** for
  pgvector → Azure AI Search. Per the auto-trigger commitment in
  Q18, the runbook must exist before the alerts could plausibly
  fire — so this is engineered in Phase 4, not when alerts trigger.

Done when: a Legal-initiated "purge all references to former-employee
X" flow walks through, librarian reviews the candidate set, hard-deletes
are applied, the wiki regenerates affected pages atomically, audit
events keep their metadata but lose content references, and a
Sentinel/Defender export confirms the SOC2-grade trail.

## Phase 5 — Polish and broader rollout (ongoing)

Pulled from a backlog as priorities dictate. The
[`future-enhancements.md`](future-enhancements.md) backlog
catalogs deliberately-deferred capabilities (especially finer-grained
access control) that v1 is designed to receive when a trigger
arrives. Promote items out of that backlog into a Phase 5 cycle as
priorities dictate.

- Teams bot for natural-language Q&A and quick-capture
- SharePoint / OneDrive connector for incremental sync
- Confluence / Notion connectors (if any departments are migrating)
- Contradiction detection in the linter (currently heuristic; promote
  to a dedicated agent)
- NotebookLM-style artifact generation (briefings, slide decks)
- Cost dashboards (per-dept token spend, retrieval cost, storage)
- Custom embedding fine-tunes if measured retrieval quality stalls
- Federated search across read-only external corpora

## Decision-Support track (runs alongside the phases above)

Per [ADR 0014](adr/0014-personas-first-class.md),
[ADR 0015](adr/0015-persona-aware-retrieval-synthesis.md), and
[ADR 0016](adr/0016-persona-internal-autonomous-actions.md), the
AI Library is a **persona-aware decision-support platform**. The
Decision-Support track lays in parallel with the foundational
phases above; each foundational phase has Decision-Support
deliverables that lift persona awareness from schema to wiring to
autonomous action over time.

### Phase 0 (foundations) — Decision-Support deliverables

- Persona schema scaffolding (in the Liquibase changelog above)
- Initial persona-roster seed data: 8 personas defined in the
  persona briefs at [`personas/`](personas/)
- The persona-neutral default (an unscoped session) is the system's
  baseline experience; persona-aware paths are off by default at
  this phase

### Phase 1 (single-department MVP) — Decision-Support deliverables

- The MCP `search` and `ask` tools accept an optional `persona`
  parameter (per [ADR 0015](adr/0015-persona-aware-retrieval-synthesis.md)
  session-time persona selection)
- The portal sign-in flow surfaces a persona selector for users
  with persona memberships
- The Engineering persona's `retrieval_profile` and
  `synthesis_style` are wired up; other personas remain at neutral
  defaults
- Persona-aware retrieval is wired but only Engineering uses non-
  default weights (per the v1 pilot scope in
  [ADR 0014](adr/0014-personas-first-class.md))

### Phase 2 (wiki layer) — Decision-Support deliverables

- The Wiki Maintainer evaluates persona-shaped facets (per
  [ADR 0006](adr/0006-llm-only-wiki-with-directives.md)
  amendment); produces an Engineering facet for pages where the
  source pool warrants it
- Spot-check linter is persona-aware: per-persona quality metrics
  start accumulating
- Audit ledger persona context (per
  [ADR 0010](adr/0010-audit-ledger.md) amendment) is fully
  wired through the `query.*` family

### Phase 3 (multimodal) — Decision-Support deliverables

- Engineering persona's source-type weights tune for code, SQL,
  meeting transcripts (Phase 3 brings these source types online)
- Per-persona evaluation framework's first iteration: golden-set
  Q&A for the Engineering persona

### Phase 4 (multi-department + RTBF) — Decision-Support deliverables

- **First Engineering persona autonomous actions transition from
  Recommend to Shadow**: ticket triage classification and routing
  are the candidates per
  [`personas/engineering.md`](personas/engineering.md)
- `persona_action_records` and `persona_action_outcomes` populated
  (Recommend mode for non-Engineering personas; Shadow mode for
  Engineering's first action set)
- Per-persona dashboards in the librarian portal show
  recommendation quality, agreement-with-human rates, and the
  state of any in-flight Recommend → Shadow → Autonomous
  promotions

### v2 (named, beyond Phase 5) — Decision-Support deliverables

- Engineering persona's first action graduates to **Autonomous**
  mode (subject to the gate thresholds in
  [ADR 0016](adr/0016-persona-internal-autonomous-actions.md))
- Product, SRE, and Customer Success personas wire up retrieval
  profiles and synthesis styles; their action sets remain in
  Recommend mode while data accumulates
- Per-persona surfaces (dashboards, digests, MCP tool variants)
  for these three personas

### v3+ — Decision-Support deliverables

- Sales, Marketing, Legal/Compliance, HR/People personas wire up
- Cross-persona synthesis (an evaluation surface; defaults to
  off until validated)
- Persona auto-detection from query intent (subject to the
  evaluation framework's labeled-data threshold in
  [`future-enhancements.md`](future-enhancements.md))

See [`decision-support-roadmap.md`](decision-support-roadmap.md)
for the per-persona rollout plan and detailed gate thresholds.

## Risks and contingencies

| Risk | Mitigation |
|---|---|
| `pgvector` performance ceiling | Partition embeddings by department; HNSW tuning. If we breach ~10M chunks, evaluate a dedicated vector store at Phase 4. |
| RLS coverage gaps | Phase 0 includes a battery of integration tests against simulated multi-department, multi-role user fixtures with classification-aware read scenarios; lint enforces RLS on every department-scoped table. |
| Misclassification at ingest exposes sensitive content company-wide | Skill plugins (PII detection, sensitive-keyword scan) flag suspect content; per-department `policy.yaml` sets `classification_default` (HR/Legal default to `Confidential`); periodic Sentinel rule audits `Internal` sources matching sensitive patterns post-approval. |
| Source-share grants accumulate stale | `source_shares.expires_at` is encouraged at grant time; librarian dashboard surfaces grants with no expiry untouched in 90 days. |
| LLM cost spiral on cascade regeneration | Per-job budget caps + nightly summaries to librarians. Hard cap = halt with a Sentinel alert. |
| Semantic Kernel version churn | Pin to a stable release; keep the gateway abstractions thin. |
| Citation validator false positives | Provide a librarian "report a bad claim" workflow that feeds back into the maintainer's training prompts. |
| Speech-to-text accuracy on domain audio | Configure Azure AI Speech custom vocabulary per department; budget time for evaluation in Phase 3. Prefer Microsoft Graph Teams transcripts (no re-transcription cost) where the source is a Teams recording. |
| Persona schema lands in v1 but most personas don't get wired up | Persona schema scaffolding in Phase 0 is intentional: the v1 pilot wires Engineering only. Other personas are seeded so v2+ work doesn't need a schema migration; persona briefs document the deferred wiring. The risk is a perception gap ("we have personas!" vs. "we have one persona wired"); mitigated by clear language in the persona briefs and the executive summary. |
| Recommend → Shadow → Autonomous promotion stalls in practice | The gate thresholds in ADR 0016 (≥30 days, ≥200/500 evaluated decisions, ≥85%/90% agreement) require real volume. Mitigation: the Engineering pilot picks ticket-triage actions where volume is naturally high; dashboards surface progress toward thresholds so stakeholders can see the gating logic working. |
| Persona facet generation costs balloon | Wiki Maintainer regenerates persona facets only when the source pool substantively differs (per ADR 0006 amendment). Phase 2 includes a budget cap per regeneration job. If costs climb regardless, the persona facet feature can be disabled per department via `policy.yaml`. |
