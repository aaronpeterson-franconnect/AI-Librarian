# Product persona

> Status: **Defined; full implementation in v2** ·
> Sponsoring department: Product · Sponsoring Persona Owner: Head
> of Product (TBD by name)

The Product persona coalesces customer signals with engineering
feasibility data to support feature prioritization, roadmap
decisions, and design choices.

## Work-context

- **Customer signal coalescence** — cluster tickets, NPS comments,
  sales-call notes, support-call transcripts, and in-app feedback
  by theme; surface trending themes, retrenching themes, and
  outlier signals
- **Feasibility correlation** — given a feature theme, surface
  related Engineering ADRs, code-area touchpoints, and prior
  feasibility analyses
- **Competitor and market intel** — synthesize market positioning
  and competitor mentions across sales calls, customer
  conversations, and external research
- **Roadmap retrospective** — what shipped vs. what was planned;
  what were the customer-impact correlates
- **Spec-to-source traceability** — given a spec, find every
  ticket, transcript, and document that fed into it

## Inputs weighted up

| Source type | Weight | Why |
|---|:---:|---|
| Customer tickets | 1.5× | Primary signal source |
| Customer call / meeting transcripts | 1.4× | Voice-of-customer with context |
| NPS / survey comments | 1.4× | Quantifiable signal anchor |
| Sales call notes | 1.3× | Pre-customer voice |
| Engineering ADRs | 1.3× | Feasibility anchor |
| Wiki pages | 1.0× | Baseline |
| Code | 0.8× | Useful for feasibility, but not the primary lens |
| Marketing collateral | 0.6× | Often forward-looking, less reliable for signal |

Recency half-life: **120 days** (Product trends accumulate over
quarters, not weeks).

## Synthesis style

| Field | Value |
|---|---|
| `answerLengthHint` | long |
| `structurePreference` | narrative |
| `citationDensity` | per-paragraph |
| `hedgingPosture` | calibrated |
| `abstentionThreshold` | 0.6 |
| `crossSourceSynthesis` | always |

Product work tolerates more cross-source synthesis and slightly
lower abstention threshold (the question "what are customers
saying about X?" rarely has a single citation; it's a synthesis
across many signals).

## Default action set (v2 → v3)

| Action ID | Description | Reversal path | v2 target | v3 target |
|---|---|---|:---:|:---:|
| `feedback.cluster` | Cluster customer-feedback signals by theme | Cluster un-merge | Recommend | Shadow |
| `theme.tag` | Tag a feedback item with one or more themes | Untag; one-row update | Recommend | Shadow |
| `feature_request.dedupe` | Identify duplicate feature requests | Untag | Recommend | Shadow |
| `feasibility.attach_adr` | Attach a relevant Engineering ADR to a feature request | Detach | Recommend | Shadow |
| `roadmap.draft_brief` | Draft a roadmap brief for a feature theme | Drop draft | Recommend | Recommend (drafts always need review) |
| `roadmap.suggest_priority` | Suggest a priority based on signal volume + feasibility | Restore prior | Recommend | Recommend (kept advisory) |

**No customer-facing actions.** No autonomous outreach to a
customer who submitted feedback (per
[ADR 0016](../adr/0016-persona-internal-autonomous-actions.md)
carve-out). A roadmap brief that suggests "we should respond to
this customer cluster" goes to a Customer Success or PM queue;
the AI does not effect the response.

## Surfaces

- **Portal**: a "Signal" workspace with theme clusters and trend
  charts; a "Feature request" surface with dedup recommendations
- **MCP**: `search`, `ask` with the Product persona parameter;
  cross-references to Engineering personas for feasibility queries
- **Digest** (v2+): weekly persona-shaped synthesis of new signal
  themes for PMs

## Evaluation

- **Per-persona golden set**: 80 Product Q&A pairs covering
  signal-clustering and feasibility-correlation queries
- **Theme-tagging agreement** with Product team consensus on a
  monthly review sample
- **Cluster quality** measured by Product team rating of "is this
  cluster coherent?"

## Pilot scope (v2)

- Wire the Product persona's retrieval profile and synthesis style
- Land the action set in Recommend mode
- Validate the per-persona golden set quality before promoting
  any action to Shadow

## See also

- [`../personas.md`](../personas.md) — Persona index
- [`engineering.md`](engineering.md) — The Engineering persona
  Product cross-references for feasibility
- [`../decision-support-roadmap.md`](../decision-support-roadmap.md)
