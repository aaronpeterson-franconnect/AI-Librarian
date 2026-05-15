# Customer Success persona

> Status: **Defined; full implementation in v2 / v3** ·
> Sponsoring department: Customer Success ·
> Sponsoring Persona Owner: VP Customer Success (TBD by name)

The Customer Success persona supports account-health management,
expansion identification, and prepared customer-facing
communication (always as a draft for human review, never
autonomous).

## Work-context

- **Account health synthesis** — combine usage signals, support
  history, NPS, and recent conversation tone into a current health
  read
- **At-risk-account flagging** — identify accounts whose health
  signals are deteriorating; surface the contributing signals
- **Expansion-opportunity identification** — pattern-match account
  signals against expansion-conducive shapes (volume growth, new
  use-case mentions, requests for capabilities the account
  doesn't yet license)
- **QBR preparation** — synthesize quarter-over-quarter signals
  for an account
- **Customer-facing draft preparation** — drafts of summary emails,
  QBR decks, and follow-up notes for CS-rep review (no autonomous
  send per [ADR 0016](../adr/0016-persona-internal-autonomous-actions.md))

## Inputs weighted up

| Source type | Weight | Why |
|---|:---:|---|
| Customer support tickets (account-scoped) | 1.5× | Primary health signal |
| Customer call transcripts (account-scoped) | 1.5× | Voice context |
| Usage telemetry / metrics | 1.4× | Quantitative health signal |
| NPS / survey responses | 1.3× | Sentiment anchor |
| Account-account history (notes, prior CS interactions) | 1.3× | Continuity |
| Product wikis | 1.0× | Baseline (capability lookup) |

Recency half-life: **45 days** for health queries; **180 days** for
QBR / account-history queries.

`floorClassification` is set to `Internal`; CS does not see HR
or Legal `Confidential` content even if the user has access via
other persona memberships.

## Synthesis style

| Field | Value |
|---|---|
| `answerLengthHint` | medium |
| `structurePreference` | bullet |
| `citationDensity` | per-claim |
| `hedgingPosture` | calibrated |
| `abstentionThreshold` | 0.65 |
| `crossSourceSynthesis` | always |

CS work pivots between operational (bullet-style hand-off lists)
and narrative (QBR prep). The bullet default is for the operational
case; QBR-specific surfaces switch to narrative.

## Default action set (v2 / v3)

| Action ID | Description | Reversal path | v2 target | v3 target |
|---|---|---|:---:|:---:|
| `account.flag_at_risk` | Flag an account based on health signals | Resolve flag | Recommend | Shadow |
| `account.flag_expansion_signal` | Flag an account showing expansion-conducive signal | Resolve flag | Recommend | Shadow |
| `account.draft_qbr_summary` | Draft a QBR summary | Drop draft | Recommend (drafts) | Recommend (drafts) |
| `account.draft_followup` | Draft a follow-up email or note | Drop draft | Recommend (drafts) | Recommend (drafts) |
| `account.tag_health_signal` | Tag an interaction with a health-signal label | Untag | Recommend | Shadow |

**No autonomous customer outreach.** All drafts go to a CS-rep
internal queue. The carve-out is permanent.

**No autonomous money / refund actions.** A CS rep handling a
refund request gets the AI's analysis ("similar past requests were
resolved like X"), suggested precedent, and a draft response — but
the binding decision and execution path are human.

## Surfaces

- **Portal**: an "Account" workspace with health view; an
  "At-risk" surface showing flagged accounts; a "QBR Prep"
  template surface
- **MCP**: `search`, `ask` with the CS persona parameter
- **CRM integration** (v3+): briefings and drafts landing as
  pre-meeting / pre-send cards in the CRM (gated by human review)

## Evaluation

- **Per-persona golden set**: 60 CS Q&A pairs covering health,
  expansion, and account-history queries
- **At-risk flag agreement** with CS rep follow-up classification
- **Draft quality** rating from CS reps post-edit-and-send

## Pilot scope (v2 / v3)

- v2: retrieval profile + synthesis style; Recommend-mode action
  set
- v3: at-risk and expansion flagging promote to Shadow if data
  warrants; draft actions stay in Recommend

## See also

- [`sales.md`](sales.md) — shared inputs and account-context
- [`product.md`](product.md) — feedback signal infrastructure
- [`../personas.md`](../personas.md)
