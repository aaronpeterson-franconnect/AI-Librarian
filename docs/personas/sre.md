# SRE / Operations persona

> Status: **Defined; full implementation in v2 / v3** ·
> Sponsoring department: SRE / Engineering Operations ·
> Sponsoring Persona Owner: Head of SRE (TBD by name)

The SRE persona supports incident response, post-incident learning,
and operational pattern detection. Closely related to the
Engineering persona but with a runbook-and-incident-first lens.

## Work-context

- **Incident triage** — given an alert or symptom, surface the
  matching runbook, prior incidents with the same symptom, and the
  on-call team's recent context
- **Post-mortem assembly** — collect and synthesize the timeline,
  affected components, contributing factors, and remediation
  decisions for an incident
- **Runbook discovery and gap analysis** — what runbooks do we
  have; what symptoms are runbook-less; which runbooks are stale
- **Operational-pattern detection** — cluster incidents by signal
  (component, timing, root-cause class) to surface recurring
  problems
- **On-call hand-off** — synthesize "what happened on the prior
  shift that the next on-call needs to know"

## Inputs weighted up

| Source type | Weight | Why |
|---|:---:|---|
| Runbooks | 1.6× | The canonical operational source |
| Post-mortems | 1.5× | Codified incident learning |
| Logs (excerpted) | 1.4× | Primary diagnostic signal |
| On-call notes / Slack threads | 1.3× | Real-time context |
| Tickets (incident-class) | 1.2× | Symptom-side detail |
| Code | 1.1× | Useful but not primary for SRE work |
| Wiki pages | 1.0× | Baseline |

Recency half-life: **45 days** for incident-class queries (current
incidents weight more heavily); **180 days** for runbook /
pattern-detection queries.

## Synthesis style

| Field | Value |
|---|---|
| `answerLengthHint` | short-to-medium |
| `structurePreference` | bullet |
| `citationDensity` | per-claim |
| `codeQuoting` | inline |
| `hedgingPosture` | direct |
| `abstentionThreshold` | 0.75 |

Incident response wants short, structured, direct answers. Higher
abstention threshold than Engineering: an SRE acting on a hedged
answer mid-incident causes more harm than admitting "I don't have
clarity on this — investigate."

## Default action set (v2 / v3)

| Action ID | Description | Reversal path | v2 target | v3 target |
|---|---|---|:---:|:---:|
| `alert.classify` | Classify an alert against the alert taxonomy | Re-classify | Recommend | Shadow |
| `alert.attach_runbook` | Attach a runbook reference to an alert | Detach | Recommend | Shadow |
| `incident.link_similar` | Link similar prior incidents on the timeline | Unlink | Recommend | Shadow |
| `oncall.handoff_summary` | Draft an on-call hand-off summary | Drop draft | Recommend | Recommend (drafts) |
| `postmortem.draft_timeline` | Draft an incident timeline from log + chat sources | Drop draft | Recommend | Recommend (drafts) |
| `runbook.gap_flag` | Flag a runbook-less symptom seen ≥3 times | Resolve flag | Recommend | Shadow |

**No autonomous remediation actions.** The persona may surface "the
runbook says restart service X" as a recommendation; the human
operator effects the restart. This is a permanent shape of the
persona, not a phase-deferred limit.

## Surfaces

- **Portal**: an "Incident" workspace; an "On-call" hand-off view;
  a "Runbook health" dashboard
- **MCP**: `search`, `ask` with the SRE persona parameter;
  optimized for the alert / incident path
- **Slack / Teams** (v3+): an SRE bot variant for in-channel
  incident assistance

## Evaluation

- **Per-persona golden set**: 60 SRE Q&A pairs covering incident
  triage, runbook lookup, and pattern queries
- **Alert-classify agreement** with on-call team review on a weekly
  sample
- **Time-to-runbook-attached** as a process-level metric

## Pilot scope (v2 / v3)

- v2: retrieval profile + synthesis style + Recommend-mode action
  set
- v3: action-set candidates promote to Shadow if v2 data warrants

## See also

- [`engineering.md`](engineering.md) — Engineering persona (close
  cousin; many SRE queries cross-reference)
- [`../personas.md`](../personas.md) — Persona index
- [`../decision-support-roadmap.md`](../decision-support-roadmap.md)
