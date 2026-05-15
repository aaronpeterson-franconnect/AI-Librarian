# Legal / Compliance persona

> Status: **Defined; full implementation in v3+** ·
> Sponsoring department: Legal · Sponsoring Persona Owner: General
> Counsel (TBD by name)

The Legal / Compliance persona supports policy applicability
research, precedent retrieval, redline assistance, and compliance-
relevant content flagging. Operates with strict abstention
discipline — Legal work tolerates "I don't know" much more readily
than incorrect or imprecise answers.

## Work-context

- **Policy applicability research** — given a question or a
  scenario, surface the relevant policy, regulatory frame, and
  internal precedent
- **Precedent retrieval** — prior contracts, prior decisions,
  prior advice on similar questions
- **Redline assistance** — drafts of contract redlines or
  policy-language suggestions, always as drafts for attorney
  review
- **Compliance-relevant content flagging** — flag content
  (sources, wiki claims, transcripts) as compliance-relevant for
  Legal review
- **RTBF / records-request assembly** — gathering the candidate
  set for a right-to-be-forgotten or records request

## Inputs weighted up

| Source type | Weight | Why |
|---|:---:|---|
| Internal policy documents | 1.6× | Primary canonical source |
| Prior contracts and addenda | 1.5× | Precedent |
| Legal-prior-decisions wiki | 1.5× | Codified Legal learning |
| Regulatory bulletins / external compliance content | 1.3× | Necessary context |
| Wiki pages | 1.0× | Baseline |
| Customer transcripts | 0.8× | Useful for some queries (records requests) |
| Marketing collateral | 0.5× | Often forward-claimable; low Legal value |

`classification_floor` is `Confidential` for many Legal queries
(Legal sources tend to be Confidential or Restricted).

Recency half-life: **365 days** (Legal precedent ages slowly).

## Synthesis style

| Field | Value |
|---|---|
| `answerLengthHint` | medium |
| `structurePreference` | bullet |
| `citationDensity` | per-claim |
| `hedgingPosture` | conservative |
| `abstentionThreshold` | 0.85 |
| `crossSourceSynthesis` | when-needed |

Highest abstention threshold in the system. Legal would rather see
"the corpus does not clearly answer this; consult a domain expert"
than a confidently-wrong synthesis. `crossSourceSynthesis: when-
needed` because Legal often wants the canonical cite, not a
synthesis across sources.

## Default action set (v3+)

| Action ID | Description | Reversal path | v3+ target |
|---|---|---|:---:|
| `content.flag_policy_relevant` | Flag content as policy-relevant | Resolve flag | Recommend |
| `content.flag_compliance_relevant` | Flag content as compliance-relevant (e.g., regulatory mention) | Resolve flag | Recommend |
| `precedent.surface` | Surface precedent for a question | n/a (read-only) | Recommend |
| `redline.draft` | Draft a redline on top of a contract clause | Drop draft | Recommend (drafts) |
| `rtbf.assemble_candidate_set` | Assemble the candidate set for an RTBF request | n/a (read-only) | Recommend |

**No autonomous Legal decisions** — all output is advisory or
drafted-for-attorney-review.

**No autonomous customer-facing communication** — per the carve-out
([ADR 0016](../adr/0016-persona-internal-autonomous-actions.md)).

**Legal-actioned RTBF** still flows through the existing concept-
RTBF workflow (per [ADR 0008](../adr/0008-tiered-deletion-and-rtbf.md));
the persona's `rtbf.assemble_candidate_set` action is a discovery aid,
not an execution path.

## Surfaces

- **Portal**: a "Policy & Precedent" workspace; an "RTBF / Records"
  workspace
- **MCP**: `search`, `ask` with the Legal persona parameter,
  particularly with the conservative hedging
- **No external integrations** in v3+; Legal output lives in the
  AI Library, not in customer-facing systems

## Evaluation

- **Per-persona golden set**: 40 Legal Q&A pairs (smaller because
  evaluation is high-touch — each pair needs Legal review)
- **Precedent-quality** rating from attorneys on review samples
- **Abstention quality**: did the system correctly say "I don't
  know" on questions that were genuinely outside the corpus

## Pilot scope (v3+)

- v3: retrieval profile + synthesis style + Recommend-mode action
  set
- v3+: action-set promotion is conservative; Legal personas may
  retain Recommend-mode indefinitely for high-stakes work

## See also

- [ADR 0008](../adr/0008-tiered-deletion-and-rtbf.md) — Tiered
  deletion and RTBF (the workflow this persona supports for
  records requests)
- [ADR 0011](../adr/0011-data-classification.md) — Classification
  (Legal sources frequently land at higher tiers)
- [`hr-people.md`](hr-people.md) — HR persona has overlapping
  policy surface
- [`../personas.md`](../personas.md)
