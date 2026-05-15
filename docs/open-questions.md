# AI Librarian — Open Questions

> Status: **Tracking** · Updated as questions resolve into ADRs

This is the list of decisions we have **not yet made** but that will
need answers before or during the relevant phase. Each one is owned and
has a target phase by which it must be resolved.

For non-engineering stakeholders (Legal, IT, Operations, Finance), the
items below are also consolidated into one-page briefs in
[`stakeholder-briefs/`](stakeholder-briefs/) — those are the documents
to send for sign-off conversations.

For deliberately-deferred capabilities (things the v1 architecture is
designed to receive *when* a real trigger arrives), see
[`future-enhancements.md`](future-enhancements.md). Open questions are
v1 decisions we have not yet made; future enhancements are post-v1
work captured so we don't lose the thread.

## Legal / compliance

### Q1. Audit retention period — POSITION DRAFTED, AWAITING LEGAL

- **Status**: position drafted; see
  [Legal brief — Decision 1](stakeholder-briefs/legal.md#decision-1--audit-retention-period)
- **Proposed position (2026-04-29)**: 7 years full content, then
  indefinite metadata-only (content references scrubbed); RTBF
  triggers immediate scrub.
- **Phase relevance**: **Phase 0 blocker** — needed before audit
  ledger schema is finalized.

### Q2. RTBF concept-level scope — POSITION DRAFTED, AWAITING LEGAL

- **Status**: position drafted; see
  [Legal brief — Decision 2](stakeholder-briefs/legal.md#decision-2--right-to-be-forgotten-concept-level-scope)
- **Proposed position (2026-04-29)**: three valid reasons (person
  data, customer cleanup, project/acquisition cleanup) with tiered
  approval thresholds. Always human-in-the-loop. Documentation
  requirement (request reference, justification, reviewer signoff)
  retained indefinitely.
- **Phase relevance**: Phase 4 — framework needs to be agreed before
  the concept-RTBF tool is built.

### Q3. Data classification levels — RESOLVED (amended)

- **Original decision (2026-04-29)**: classification as a **label,
  not an access boundary**.
- **Amendment (2026-04-29, same day)**: flipped to classification
  **as the default access boundary**. `Internal` (default) is
  readable by any authenticated employee; `Confidential` is gated
  to the owning department; `Restricted` to the owning department's
  `Librarian`+. `source_shares` provides explicit cross-department
  exceptions for `Confidential` / `Restricted` sources. Department
  membership remains the only gate for write authority. The
  amendment was driven by the realization that strict per-department
  isolation defeats real-world cross-department collaboration on
  routine knowledge.
- See [ADR 0011 (amended)](adr/0011-data-classification.md) for
  the full model and [ADR 0005 (amended)](adr/0005-rls-with-entra.md)
  for the RLS predicates.

### Q4. Cross-border data residency — POSITION DRAFTED, AWAITING LEGAL

- **Status**: position drafted; see
  [Legal brief — Decision 3](stakeholder-briefs/legal.md#decision-3--cross-border-data-residency)
- **Proposed position (2026-04-29)**: single US Azure region
  (East US 2 default) for v1; per-department region binding deferred
  to Phase 5+ if a future requirement forces it.
- **Phase relevance**: **Phase 0 blocker** — Azure region cannot be
  picked without this direction.

## Identity and access

### Q5. Entra group naming convention — RESOLVED

- **Decision (2026-04-29)**: follow IT's existing naming convention
  for app-scoped Entra groups. The system reads the (department,
  role) → group mapping from configuration and treats group names as
  opaque strings, so we can adapt to any IT-side standard without
  code changes.
- **Outstanding**: IT to publish the canonical pattern for
  AI Librarian groups (likely a small variation on existing
  app-group naming). Captured in the IT runbook for Phase 0
  pre-work.

### Q6. Workstation MCP authentication — RESOLVED

- **Decision (2026-04-29)**: ship a small cross-platform .NET binary
  **`AiLibrarian.Cli`** that handles Entra device-code OAuth on first
  run, caches refresh tokens in OS-native secure storage (DPAPI /
  Keychain / libsecret), and exposes the MCP server as a localhost
  stdio bridge that workstation AI clients point at.
- **Why this over device-code in each client**: one place to manage
  workstation concerns (token storage, log forwarding, version
  checks), better attribution in the audit ledger, and a consistent
  UX across Cursor / Claude Desktop / Copilot / future clients.
- **Implication**: the CLI is a Phase 1 deliverable. Web portal and
  Teams bot do not need it; they use standard server-side OAuth.

### Q7. External / contractor access — POSITION DRAFTED, AWAITING IT

- **Status**: position drafted; see
  [IT brief — Decision 1](stakeholder-briefs/it.md#decision-1--external--contractor-access)
- **Proposed position (2026-04-29)**: **Pattern B (B2B guests,
  read-only)** for v1. Externals can be added to a department's
  `Reader` group via standard B2B guest provisioning; cannot be
  Contributor / Reviewer / Librarian. Pattern C (guest as
  Contributor) deferred to Phase 5+ if a real use case emerges.
- **Phase relevance**: Phase 4.

## Ingestion and content

### Q8. Embedding model for v1 — RESOLVED

- **Decision (2026-04-29)**: **Azure OpenAI `text-embedding-3-large`**.
  Highest quality, regional residency in our Azure tenant, and the
  per-call cost is acceptable at projected volumes (~$200-400 for
  the initial year-1 corpus, ongoing cost dominated by query
  embeddings rather than ingest).
- **Implication**: if we re-evaluate at year 2/3 (when chunk volume
  approaches `pgvector`'s ceiling per Q18), a model change forces a
  one-time re-embedding event. Plan for that.
- **Local-model fallback**: kept as an option in `AiLibrarian.LlmGateway`
  via a disabled-by-config Ollama connector (per ADR 0003) so we can
  pivot if cost or residency requirements change.

### Q9. Chunk size and strategy per format — DEFAULTS LOCKED, TUNING OPEN

- **Defaults accepted (2026-04-29)**:

	| Skill | Strategy | Default size |
	|---|---|---|
	| Markdown / plain | Paragraph-grouped | ~500 tokens, 50-token overlap |
	| PDF | Per-page with paragraph boundaries | ~800-1200 tokens, no overlap |
	| DOCX | Per-section if available, else paragraph-grouped | ~500-800 tokens |
	| Code | Semantic (per function/class) via Roslyn / TreeSitterSharp | Function-bounded |
	| SQL | Per Liquibase changeSet; else per statement group | changeSet-bounded |
	| Audio / video | Per speaker turn with diarization | ~30-60 s, turn-bounded |
	| Images | One chunk per detected region (diagrams) or per-image | Region-bounded |

- **Open**: empirical tuning in Phase 3 based on retrieval-quality
  telemetry (recall@k, MRR). Phase 3 work is optimization, not
  decision.
- **Owner**: Architect (with retrieval-quality telemetry from Phase 1)
- **Target phase**: Phase 3 (tuning)

### Q10. Source size limits — RESOLVED

- **Decision (2026-04-29)**: configurable per-department in
  `policy.yaml`, with these system-wide defaults:

	| Format | Soft warn | Hard cap |
	|---|---|---|
	| Plain text / markdown | 5 MB | 10 MB |
	| PDF | 50 MB | 100 MB |
	| DOCX / XLSX / PPTX | 25 MB | 50 MB |
	| Code (per file) | 1 MB | 5 MB |
	| Code repo ingest (total) | 250 MB | 500 MB |
	| Images | 10 MB | 25 MB |
	| Audio | 100 MB | 250 MB (~2 h at 256 kbps) |
	| Video | 1 GB | **2 GB** (~2 h at 720p MP4) |
	| Zip archive | 500 MB | 1 GB |

- **Soft-warn behavior**: ingest proceeds but the source is flagged
  for librarian review.
- **Hard-cap behavior**: ingest rejected with a clear error message
  recommending the source be split or processed externally.

### Q11. Email-drop and connector authentication — POSITION DRAFTED, AWAITING IT

- **Status**: position drafted; see
  [IT brief — Decision 2](stakeholder-briefs/it.md#decision-2--email-drop-and-connector-authentication)
- **Proposed position (2026-04-29)**:
	- **Email drop**: Option C — reject non-employee senders;
	  verified employee senders treated as the submitting
	  Contributor for the matching department.
	- **Connectors**: Option F (hybrid) — service-principal default
	  with last-modified-by attribution where available.
- **Phase relevance**: Phase 5 (deferred from v1).

## Wiki authoring and governance

### Q12. Directive precedence rules — RESOLVED

- **Decision (2026-04-29)**: precedence is **priority >
  scope-specificity > recency**.
	- A `priority: high` directive always wins over `medium` or
	  `low`, regardless of scope or age.
	- Among equal priorities, narrower scope wins (page-glob beats
	  department-wide).
	- Among equal priority and scope, newest wins.
	- Two directives at same priority + same scope that contradict
	  → maintainer logs a `directive.conflict` audit event, proceeds
	  with the newer, surfaces the conflict in the librarian
	  dashboard.
- **Priority tiers**: `low`, `medium` (default), `high`. `high` is
  reserved for librarian-issued urgent corrections.

### Q13. Page-lock SLAs — RESOLVED

- **Decision (2026-04-29)**:
	- Default proposed-revision expiry: **14 calendar days** if no
	  Reviewer/Librarian decision. On expiry: auto-rejected with
	  reason `"expired without review"`.
	- Soft escalation at **5 business days** unattended → notify
	  the Librarian role group for the department + surface a
	  "queue health" card on the librarian dashboard.
	- Hard escalation at **10 business days** unattended → notify
	  Admin.
	- Per-department override allowed in `policy.yaml`.
- **Implementation note**: lock SLAs share the same constants as
  approval-queue SLAs (Q17) so librarians have one mental model.

### Q14. Cross-department links — RESOLVED (amended)

- **Original decision (2026-04-29)**: render-time gating fires
  for any cross-department target the caller can't access.
- **Amendment (2026-04-29, same day)**: after Q3 / ADR 0011 was
  flipped to classification-as-access-boundary, render-time
  gating now fires only for **inaccessible Confidential or
  Restricted targets**. Most cross-department links are to
  `Internal` content and render normally. Specifically:
	- **Internal link target**: rendered normally for any
	  authenticated employee.
	- **Confidential / Restricted link target the caller cannot
	  access**: rendered as `[restricted: {DepartmentName}]`.
	- **Unauthorized citation source**: rendered as
	  `[citation: restricted]`; the claim remains visible if it
	  appears in a facet the caller can read.
	- **`get_neighborhood`**: returns inaccessible linked pages as
	  `{ id, department_name, status: "restricted" }` only.
	- **Page facets** (per [ADR 0006](adr/0006-llm-only-wiki-with-directives.md)):
	  `read_page` returns the highest-classification facet the
	  caller can access; the existence of higher facets is
	  signaled with a small footer note but never their content.
- See [ADR 0004 (amended)](adr/0004-mcp-as-single-access-layer.md)
  for the full rendering rules.

## Operations

### Q15. Cost caps and budgets — POSITION DRAFTED, AWAITING OPS/FINANCE

- **Status**: position drafted; see
  [Ops/Finance brief — Decision 1](stakeholder-briefs/operations-finance.md#decision-1--cost-caps-and-budgets)
- **Proposed position (2026-04-29)**: per-department default
  $2,500/month with 50/80/100% tiered behaviors; system-wide
  backstop at 5× sum of department caps; Librarian/Admin can
  grant mid-month overrides with audit trail.
- **Phase relevance**: Phase 4 — cost-cap mechanisms ship in
  Phase 4; Phase 0 has full cost telemetry without caps.

### Q16. DR RPO/RTO targets — POSITION DRAFTED, AWAITING OPS

- **Status**: position drafted; see
  [Ops/Finance brief — Decision 2](stakeholder-briefs/operations-finance.md#decision-2--disaster-recovery-rpo--rto)
- **Proposed position (2026-04-29)**: RPO 1 hour, RTO 4 hours
  same-region / 8 hours cross-region. Achieved via Postgres PITR
  (35-day retention, geo-redundant) + GRS Blob. Region pair
  East US 2 ↔ Central US. Quarterly DR exercise.
- **Phase relevance**: Phase 4.

### Q17. Approval-queue librarian SLAs — RESOLVED

- **Decision (2026-04-29)**:
	- Target P50 first-decision: **24 business hours**
	- Target P90 first-decision: **5 business days**
	- Soft escalation at **5 business days** unattended → notify
	  the Librarian role group + surface "queue health" card on
	  the librarian dashboard.
	- Hard escalation at **10 business days** unattended → notify
	  Admin.
	- Auto-reject pending sources at **14 calendar days**
	  unattended (audit retained, queue cleaned).
	- Per-department override allowed.
- **Implementation note**: same constants as Q13 by design.

### Q18. Vector-store migration trigger — RESOLVED

- **Decision (2026-04-29)**:
	- **Continuous alerting**: Sentinel fires on threshold breaches:
		- p95 vector-search latency > 500 ms sustained over 7 days
		- HNSW rebuild for a single department > 30 minutes
		- Active vector count > 8M on a single instance
		- Department-partitioned index build memory pressure exceeds
		  the instance class
	- **Quarterly trend review**: every quarter, Operations reviews
	  the trend even if no thresholds tripped, so we see growth
	  trajectory before it becomes urgent.
	- **Auto-trigger migration project on any 2 of 4 thresholds
	  exceeded simultaneously**: the migration project starts (team
	  pulled in, runbook executed, dual-write to AI Search begins).
	  The migration *project* is auto-triggered; the migration
	  itself follows a documented runbook with human approval at
	  each cutover stage.
- **Migration target**: Azure AI Search (primary), Qdrant (backup).
- **Pre-requisite for auto-trigger to work**: the migration runbook,
  dual-write scripts, and validation tests must be authored in
  Phase 4 — *before* the alerts could plausibly fire — so the
  auto-trigger isn't "discover what to do" but "execute the
  pre-engineered plan." This becomes a Phase 4 deliverable.
