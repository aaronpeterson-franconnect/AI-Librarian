# AI Librarian — Future enhancements

> Status: **Tracking** · Forward-looking backlog of "things we may add later"
>
> Distinct from [`open-questions.md`](open-questions.md): open questions are
> v1 decisions we have not yet made. Future enhancements are **deliberately
> deferred** capabilities that the v1 architecture is designed to receive
> when a real trigger arrives.

This document is a place to capture good ideas without polluting v1 scope
or the open-questions tracker. Items here have:

- A clear **trigger scenario** — when would we actually pull this lever?
- A **v1-compatible upgrade path** — what specifically needs to change to
  add it?
- An **owner candidate** — who would drive it when the trigger arrives?

Items are not prioritized; that happens when a trigger fires.

> **Scope reminder.** AI Librarian is hyperscaler-only — Azure or AWS.
> On-prem, air-gapped, and customer-datacenter deployments are
> permanently out of scope per
> [ADR 0013](adr/0013-hyperscaler-deployment-scope.md). Enhancements
> proposed here should respect that boundary. A proposal to lift it
> is itself an ADR amendment, not a future-enhancement entry.
>
> **Permanent carve-out reminder.** Per
> [ADR 0016](adr/0016-persona-internal-autonomous-actions.md), no
> autonomous customer-facing actions and no AI-direct money/refund
> decisions are permitted in any persona's action set. These are
> **not** future enhancements; they are structural commitments. A
> proposal to lift them is a new ADR with Legal sign-off, not a
> future-enhancement entry. Recommend/analyze/draft modes for
> money/refund-adjacent work remain available; the constraint is on
> binding decisions and customer-facing communication paths.

## Access control

Detailed in [ADR 0011 — Future enhancements](adr/0011-data-classification.md#future-enhancements-deferred).

### Read-side

| # | Enhancement | Trigger | Owner candidate |
|---|---|---|---|
| AC-R1 | Per-user source share | Auditor / executive needs single-source access without department membership | Architect + IT |
| AC-R2 | Group-based source share (e.g., All-Librarians) | A Legal template needs to be referenceable by any department's librarian | Architect |
| AC-R3 | Project / initiative scope | A cross-functional project team spans 3+ departments and needs scoped visibility | Architect + project sponsor |
| AC-R4 | Sub-classifications (`Confidential-PII`, `Confidential-Financial`) | Differentiated retention, audit, or reviewer routing within a tier | Architect + Legal |
| AC-R5 | Chunk-level classification | A long PDF with mixed-sensitivity sections | Architect |
| AC-R6 | Claim-level visibility within a facet | A Confidential facet contains a few claims that should be Restricted-only | Architect |
| AC-R7 | Need-to-know click-through acknowledgment | A Restricted source requires NDA-style ack before first read | Legal + Architect |
| AC-R8 | Time-bounded per-user grants | Time-limited engagement access (90-day auditor) | Architect |

### Authority side

| # | Enhancement | Trigger | Owner candidate |
|---|---|---|---|
| AC-A1 | Cross-department reviewer pools | A Legal-reviewer queue any Legal-reviewer can pick up regardless of source's home department | Architect + Legal |
| AC-A2 | Department sub-structures (re-introduce hierarchy) | "Backend within Engineering" becomes a real concept needing inherited access | Architect |
| AC-A3 | ABAC attributes | Need to gate on attributes beyond department/role (manager status, tenure, PCI-trained) | Architect + IT |

### Process

| # | Enhancement | Trigger | Owner candidate |
|---|---|---|---|
| AC-P1 | Auto-reclassification | Sources age out of sensitivity; project codename becomes public post-launch | Architect |
| AC-P2 | External / B2B per-source shares | Contractors / customers / partners need scoped access | IT + Architect (Q7 is the precondition) |
| AC-P3 | Per-source forensic watermarking | Compliance requirement for byte/page-level read tracking | Compliance + Architect |

## Retrieval and AI behavior

| # | Enhancement | Trigger | Owner candidate |
|---|---|---|---|
| RAI-1 | Persona auto-detection from query intent | ≥1,000 evaluated queries per persona accumulate so a detector can be trained and evaluated meaningfully | Architect + Persona Owners |
| RAI-2 | Cross-persona synthesis | A class of queries genuinely needs blended persona retrieval (Product question with Engineering signal weighting) and v2 evaluation framework can validate the blend | Architect + Persona Owners |
| RAI-3 | Data-driven retrieval-profile tuning | A persona accumulates ≥1,000 evaluated queries; per-persona telemetry (CTR, thumbs-up/down, override rate) supports automated retuning | Architect |
| RAI-4 | Per-persona LoRA / fine-tunes | Heuristic + tuned-retrieval profile hits a quality ceiling that LoRA could plausibly lift; cost-benefit case clears the bar | Architect + Persona Owners |
| RAI-5 | Custom embedding fine-tunes | Measured retrieval quality stalls and per-domain content composition makes generic embeddings insufficient | Architect |
| RAI-6 | Semantic cache for repeated queries | Persona-shaped cache of recent answer skeletons; useful at scale; may interact with persona-action audit | Architect |
| RAI-7 | Multi-step agentic retrieval | Queries that need decomposition + iteration outpace what single-pass retrieval does well | Architect |
| RAI-8 | Query-rewrite layer | Persona-shaped query rewrites (e.g., a Sales-persona query gets rewritten with account context) lift retrieval quality | Architect |

## UX and portal

Placeholder. Examples: side-by-side facet diff for librarians, sandbox
mode for testing directives, anomaly heatmaps, contributor-of-the-week
dashboards.

## Governance and compliance

Placeholder. Examples: automated retention reviews, classification-
drift reports, regulatory-mode toggles (HIPAA / GDPR / SOX-strict),
content-aging notifications.

## Persona model

These are deliberately deferred persona-related capabilities.
Capabilities that touch the **permanent carve-outs** (autonomous
customer-facing actions, AI-direct money/refund decisions) are
**not** entries here — those require new ADRs with Legal sign-off,
not future-enhancement entries.

| # | Enhancement | Trigger | Owner candidate |
|---|---|---|---|
| P-1 | Cross-persona conflict resolution | A query genuinely contradicts across personas (e.g., the Product persona's source pool and the Engineering persona's source pool give different answers); needs an evaluation framework before resolution policy can be set | Architect + Persona Owners |
| P-2 | Persona-as-LoRA / per-persona fine-tunes | (See RAI-4 above; persona-specific item) | Architect + Persona Owners |
| P-3 | Cross-organization persona templates | A persona definition (retrieval profile + synthesis style + action set) becomes shareable as a template that other companies could adopt as a starting point | Architect |
| P-4 | Persona-aware prompt-rewriting | Personas inform query reformulation in addition to retrieval and synthesis | Architect |
| P-5 | Auto-promotion of action modes | The recommend → shadow → autonomous gates run on telemetry without human Persona-Owner sign-off when thresholds clear (currently sign-off is required) | Architect + Persona Owners |

## How to add an item

1. Add it to the appropriate category table above with trigger and owner
   candidate.
2. If it touches access control, also add it to the ADR 0011
   "Future enhancements" section so the upgrade path is captured next
   to the architecture itself.
3. If a real trigger fires, promote the item out of this doc into:
   - An open question (if the design is unsettled)
   - A new ADR (if it's a distinct architectural commitment)
   - A phase deliverable (if it's a sized chunk of work)
