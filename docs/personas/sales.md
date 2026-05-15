# Sales persona

> Status: **Defined; full implementation in v2 / v3** ·
> Sponsoring department: Sales · Sponsoring Persona Owner: VP
> Sales (TBD by name)

The Sales persona supports account-prep, talk-track development,
competitive positioning, and deal-context synthesis.

## Work-context

- **Account briefings** — synthesize what we know about an
  account: prior conversations, support history, product usage
  signals, key contacts, prior decisions, open opportunities
- **Talk-track preparation** — given a customer profile, surface
  the relevant case studies, competitive comps, objection handling
  notes, and product positioning
- **Competitive intelligence** — when a competitor is mentioned,
  surface what we know: prior win/loss, public claims,
  positioning angles
- **Deal-stage queries** — "what did we promise this customer in
  the December conversation?" with citations into the transcript
- **Product-roadmap state for sales** — what's shipping, what's
  drafted, what's still firmly on the roadmap (with feasibility
  caveats from Engineering data)

## Inputs weighted up

| Source type | Weight | Why |
|---|:---:|---|
| Sales-call transcripts | 1.5× | Direct customer voice in deal context |
| Customer conversations / emails | 1.4× | Account-specific signal |
| Win/loss analyses | 1.4× | Codified deal learning |
| Customer tickets (the account's own) | 1.3× | Account-specific support context |
| Marketing collateral | 1.2× | Authoritative for current positioning |
| Product wikis | 1.1× | Authoritative for product capability |
| Competitor analyses | 1.1× | Useful with calibrated freshness |

`crossDepartmentBoost` for shared sources is set high (1.3×);
sales context often spans Customer Success, Marketing, and
Engineering.

Recency half-life: **60 days** (deal context shifts quickly).

## Synthesis style

| Field | Value |
|---|---|
| `answerLengthHint` | medium |
| `structurePreference` | narrative |
| `citationDensity` | per-paragraph |
| `hedgingPosture` | calibrated |
| `abstentionThreshold` | 0.6 |
| `crossSourceSynthesis` | always |

Sales prep tolerates calibrated hedging (the rep can supply
their own confidence about what's actionable).

## Default action set (v2 / v3)

| Action ID | Description | Reversal path | v2 target | v3 target |
|---|---|---|:---:|:---:|
| `account.draft_briefing` | Draft an account briefing for an upcoming meeting | Drop draft | Recommend | Recommend (drafts always need review) |
| `account.tag_competitor_mention` | Tag a competitor mention in a transcript | Untag | Recommend | Shadow |
| `account.flag_at_risk` | Flag an account as at-risk based on signals | Resolve flag | Recommend | Shadow |
| `talk_track.suggest_objection_handling` | Suggest objection-handling material for a known objection class | Restore prior | Recommend | Recommend |
| `winloss.cluster` | Cluster recent win/loss data by theme | Cluster un-merge | Recommend | Shadow |

**No autonomous customer outreach** (per the carve-outs). All
draft outreach is internal-queue only.

**No money/refund-touching actions** in the action set: a deal-
specific discount or contract-credit recommendation is *advice*
that lands in a human pricing-approval flow; the AI does not
autonomously price.

## Surfaces

- **Portal**: an "Account" workspace surfacing a briefing template
  ahead of meetings; a "Win/loss themes" dashboard
- **MCP**: `search`, `ask` with the Sales persona parameter
- **CRM-integration** (v3+): briefings landing in the CRM as
  pre-meeting cards (still gated by human review)

## Evaluation

- **Per-persona golden set**: 50 Sales Q&A pairs covering account
  context, competitor lookup, and product-state queries
- **Briefing-quality** rating from sales reps post-meeting

## Pilot scope (v2 / v3)

- v2: retrieval profile + synthesis style; action set in Recommend
- v3: action-set promotions if v2 data warrants

## See also

- [`customer-success.md`](customer-success.md) — close cousin
- [`marketing.md`](marketing.md) — shared inputs (positioning,
  win/loss)
- [`../personas.md`](../personas.md)
