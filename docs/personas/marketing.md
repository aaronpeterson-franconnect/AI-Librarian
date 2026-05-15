# Marketing persona

> Status: **Defined; full implementation in v3** ·
> Sponsoring department: Marketing · Sponsoring Persona Owner: VP
> Marketing (TBD by name)

The Marketing persona synthesizes customer voice, competitive
mentions, win/loss themes, and product-state changes into
positioning-relevant insight.

## Work-context

- **Voice-of-customer synthesis** — how customers describe the
  product in their own words; clusters of language; sentiment
  trends
- **Competitor mention analysis** — across sales calls, support
  conversations, and external research; what's said, by whom,
  and how often
- **Win/loss theme aggregation** — what reasons recur in won and
  lost deals; how those reasons are evolving
- **Product-change positioning** — when Engineering ships a
  feature, what existing positioning needs to update
- **Campaign-effectiveness retrospective** — pulling threads from
  multiple data sources to evaluate a campaign's effect

## Inputs weighted up

| Source type | Weight | Why |
|---|:---:|---|
| Customer transcripts | 1.5× | Voice-of-customer primary source |
| NPS / survey verbatims | 1.4× | Quantifiable customer voice |
| Sales-call transcripts | 1.3× | Pre-customer voice with context |
| Win/loss analyses | 1.3× | Deal-themed customer voice |
| Customer support tickets | 1.2× | Pain-point signal |
| Marketing collateral | 0.8× | Less informative for *new* positioning |

Recency half-life: **180 days** for thematic synthesis (positioning
evolves slowly); **30 days** for campaign-effectiveness queries.

## Synthesis style

| Field | Value |
|---|---|
| `answerLengthHint` | long |
| `structurePreference` | narrative |
| `citationDensity` | per-paragraph |
| `hedgingPosture` | calibrated |
| `abstentionThreshold` | 0.6 |
| `crossSourceSynthesis` | always |

Marketing synthesis is inherently many-to-one; a single citation
rarely carries the full theme.

## Default action set (v3)

| Action ID | Description | Reversal path | v3 target |
|---|---|---|:---:|
| `voc.theme_cluster` | Cluster voice-of-customer signals into themes | Cluster un-merge | Recommend |
| `competitor.tag_mention` | Tag a competitor mention with sentiment + context | Untag | Recommend |
| `winloss.theme_aggregate` | Aggregate win/loss reasons into themes | Cluster un-merge | Recommend |
| `positioning.draft_update` | Draft a positioning update on top of a product change | Drop draft | Recommend (drafts) |

All actions land in Recommend in v3; promotion to Shadow waits
for evaluation evidence.

**No autonomous customer-facing actions.** Positioning drafts go
to a Marketing internal queue; no auto-publish, no auto-send.

## Surfaces

- **Portal**: a "Voice of customer" dashboard with theme clusters
- **MCP**: `search`, `ask` with the Marketing persona parameter
- **Digest** (v3+): monthly thematic synthesis for marketing
  leadership

## Evaluation

- **Per-persona golden set**: 50 Marketing Q&A pairs covering
  thematic queries, competitor lookups, and positioning lookups
- **Theme coherence** rating from marketing leadership on monthly
  review samples

## Pilot scope (v3)

- Wire retrieval profile and synthesis style
- Land Recommend-mode action set
- Validate per-persona quality before considering Shadow promotion
  in v4+

## See also

- [`product.md`](product.md) — shared customer-signal infrastructure
- [`sales.md`](sales.md) — shared win/loss and competitor inputs
- [`../personas.md`](../personas.md)
