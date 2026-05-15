# HR / People persona

> Status: **Defined; full implementation in v3+** ·
> Sponsoring department: HR / People · Sponsoring Persona Owner:
> Head of People (TBD by name)

The HR persona supports policy lookup, anonymous-feedback theme
synthesis (with strict classification handling), training-need
analysis, and role-related content discovery. Most HR sources are
sensitive; this persona is the most classification-sensitive in the
v1 roster.

## Work-context

- **Policy and benefit lookup** — given a question, surface the
  authoritative HR policy, benefit description, or process
  documentation
- **Anonymous-feedback theme synthesis** — cluster anonymous
  survey responses or feedback into themes for People leadership
  (with strict aggregation rules so no single response is
  identifiable)
- **Training-need analysis** — pattern-match across feedback,
  manager notes, and observed performance signals to surface
  training and development opportunities
- **Role-related lookup** — given a role, surface the role's
  defined responsibilities, expected outcomes, and relevant
  policies
- **Process navigation** — given a People-process question
  (onboarding, termination, leave), surface the canonical process
  documentation

## Inputs weighted up

| Source type | Weight | Why |
|---|:---:|---|
| HR policy documents | 1.6× | Primary canonical source |
| Anonymous feedback (aggregated) | 1.4× | Voice-of-employee anchor |
| Manager training documents | 1.3× | Codified People practice |
| Wiki pages | 1.0× | Baseline |
| External People research / vendor content | 0.8× | Calibrated freshness |

`classification_floor` is `Confidential` — HR queries default to
Confidential-and-above sources. `Restricted` HR content is
available to HR Librarian+ users only (already true under
classification rules).

Recency half-life: **270 days** (HR policy and practice evolve
slowly).

## Synthesis style

| Field | Value |
|---|---|
| `answerLengthHint` | medium |
| `structurePreference` | narrative |
| `citationDensity` | per-claim |
| `hedgingPosture` | conservative |
| `abstentionThreshold` | 0.8 |
| `crossSourceSynthesis` | when-needed |

HR work tolerates very little hedging-on-policy. Conservative
posture and high abstention threshold are appropriate.

## Anonymous-feedback handling

Anonymous-feedback aggregation requires special care:

- A theme cluster surfaces only if it has **≥ 5 contributing
  responses** (k-anonymity floor at 5)
- Verbatim quotes from anonymous feedback are **not** surfaced in
  the persona's output; only paraphrases and counts
- The Wiki Maintainer's anonymous-feedback handling policy (v3+
  Phase 2 of this persona) follows the People team's documented
  privacy policy, with the AI subject to an explicit prompt-time
  rule against attribution
- Persona action records on anonymous-feedback content classify
  the action target by feedback-batch ID, not by individual
  respondent ID

These rules are operational; they layer on top of the structural
classification + RLS protection.

## Default action set (v3+)

| Action ID | Description | Reversal path | v3+ target |
|---|---|---|:---:|
| `policy.surface_relevant` | Surface relevant HR policy for a question | n/a (read-only) | Recommend |
| `feedback.theme_cluster` | Cluster anonymous feedback into themes (k-anonymity ≥ 5) | Cluster un-merge | Recommend |
| `training.suggest_module` | Suggest training material for a manager / IC | Restore prior | Recommend |
| `process.surface_runbook` | Surface a People-process runbook (onboarding, leave, etc.) | n/a (read-only) | Recommend |

**No autonomous HR decisions.** This persona does not action
performance, compensation, or employment decisions in any mode.
Any output is advisory.

**No autonomous customer-facing communication** — already covered
by the carve-out; HR rarely faces customers, so the carve-out is
mostly nominal here, but the structural protection remains.

## Surfaces

- **Portal**: a "HR Policy" workspace; a "Feedback Themes" view
  (privacy-aware); a "Process Help" surface
- **MCP**: `search`, `ask` with the HR persona parameter,
  conservative hedging
- **No external integrations** in v3+; HR output stays inside the
  AI Library

## Evaluation

- **Per-persona golden set**: 40 HR Q&A pairs (smaller and
  high-touch like Legal)
- **Policy-correctness** rating from People-team review samples
- **Privacy-leak audit**: an offline test that anonymous-feedback
  output never re-identifies a respondent against ground-truth
  data

## Pilot scope (v3+)

- v3+: retrieval profile + synthesis style + Recommend-mode action
  set
- Indefinite Recommend mode for action types touching anonymous
  feedback; promotion to Shadow only on validation by a privacy
  review

## See also

- [ADR 0011](../adr/0011-data-classification.md) — Classification
  (HR sources default high)
- [`legal-compliance.md`](legal-compliance.md) — overlapping
  policy surface
- [`../personas.md`](../personas.md)
