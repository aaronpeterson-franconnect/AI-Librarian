# Engineering persona — v1 pilot

> Status: **Pilot in v1** · Sponsoring department: Engineering ·
> Sponsoring Persona Owner: Engineering Director (TBD by name)

The Engineering persona is the v1 pilot for the persona model
(per [ADR 0014](../adr/0014-personas-first-class.md)). It's the
first persona to land with non-default retrieval profile and
synthesis style, and the first to run the
recommend → shadow → autonomous progression on its action set.

## Work-context

Engineering work the persona supports:

- **Triage** — given a customer ticket, error, or incident,
  identify the relevant code, similar prior incidents, and
  applicable runbook
- **Code-grounded answers** — "how does the auth flow handle
  expired refresh tokens in the staging environment?" returns an
  answer grounded in the code, not in marketing docs
- **Runbook attachment** — surface the right runbook for the
  symptom in front of the engineer
- **Architecture and design lookup** — prior ADRs, design docs,
  RFCs, and engineering wikis
- **Cross-source synthesis for engineers** — a question that
  touches code, SQL, runbooks, and Slack/Teams conversations is
  synthesized in a code-first way
- **Standards / pattern lookup** — "how do we handle background
  jobs in this codebase?" returns the codified pattern, not the
  retrospective on the time we got it wrong

## Inputs the persona weights up

| Source type | Weight | Why |
|---|:---:|---|
| Code | 1.5× | Engineering work is code-grounded; code is canonical truth for "how does this actually work?" |
| Runbooks | 1.4× | High signal-to-noise for operational and incident questions |
| SQL (Liquibase) | 1.3× | Schema and migration history are part of "how things work" |
| Tickets | 1.2× | Recent tickets surface real-world bugs and patterns |
| Wiki pages | 1.0× | Baseline; the wiki itself is well-cited |
| Meeting transcripts | 0.9× | Useful but lossy; engineering decisions are usually re-stated in writing |
| Email | 0.7× | Often outdated; rarely the canonical source for engineering questions |
| Image | 0.6× | Diagrams help, but engineering rarely needs the diagram primary |

Recency half-life: **90 days** for the default; tighter for
incident-context queries (30 days). Older code has stable canonical
status and isn't down-weighted by recency alone.

Authority bias:

- `canonical` (e.g., a `CODEOWNERS`-pointed module, a wiki page
  marked canonical) — 1.5×
- `current` — 1.0×
- `superseded` — 0.3×
- `draft` — 0.5×

## Synthesis style

| Field | Value |
|---|---|
| `answerLengthHint` | medium |
| `structurePreference` | code-first |
| `citationDensity` | per-claim |
| `codeQuoting` | preserve-context |
| `hedgingPosture` | direct |
| `abstentionThreshold` | 0.7 |
| `crossSourceSynthesis` | always |
| `showSourceMetadata` | true |

The Engineering persona prefers the system **says "I don't know"
often** rather than hedging through low-confidence answers. The
abstention threshold is the same as the citation contract floor —
no novel calibration, just consistent application.

`code-first` synthesis means: when the question concerns code, lead
with the code snippet (citation-bound), then the surrounding
explanation. When it concerns process, lead with the runbook step.

## Default action set (v1 → v2)

All actions start in **Recommend** mode in v1 (Phase 4 deliverable).
Promotion targets are listed; the actual mode-changes happen via
the gate in [ADR 0016](../adr/0016-persona-internal-autonomous-actions.md).

| Action ID | Description | Reversal path | v1 (Phase 4) | v2 target |
|---|---|---|:---:|:---:|
| `ticket.classify` | Apply a category label from the team's taxonomy | Re-classify; one-row update + audit | Recommend | **Shadow** then Autonomous |
| `ticket.route` | Route a ticket to the appropriate sub-team or persona | Re-route; restore prior assignment | Recommend | **Shadow** then Autonomous |
| `ticket.attach_runbook` | Attach a relevant runbook reference | Detach; one-row update | Recommend | **Shadow** then Autonomous |
| `ticket.link_similar` | Link similar prior incidents | Unlink; row delete | Recommend | **Shadow** |
| `ticket.priority_suggest` | Suggest a priority based on signals | Restore prior priority | Recommend | **Recommend** (kept advisory) |
| `incident.surface_pattern` | Cluster recent incidents by symptom | Cluster un-merge; recompute | Recommend | **Recommend** (kept advisory) |
| `code.suggest_owner` | Suggest the right reviewer / owner | Override; one-row update | Recommend | **Shadow** |
| `runbook.draft_update` | Draft a proposed runbook update on top of an incident | Drop draft; never auto-publishes | Recommend | **Recommend** (drafts always need review) |

**No customer-facing actions appear in this set.** The carve-outs
in [ADR 0016](../adr/0016-persona-internal-autonomous-actions.md)
are the structural reason — Engineering work is internal-team-
facing by definition for this persona, but the carve-out is what
guarantees a future addition can't slip a customer-facing action in
without ADR-level review.

**No money / refund actions appear in this set.** Engineering
tickets sometimes touch billing, but the persona's autonomous
authority does not extend to money decisions even in
recommend-mode automation that *triggers* a money move (e.g.,
auto-issuing a credit). Recommendations on billing-adjacent
tickets land in the Customer Success or Finance queue for human
decision.

## Surfaces

- **MCP**: `search`, `ask`, `cite`, `get_page`, `get_neighborhood`
  with the Engineering persona parameter
- **Portal**: a "Triage" workspace surfacing pending tickets with
  Recommend-mode suggestions; an "Ask the codebase" search surface
  with code-first synthesis
- **CLI**: `AiLibrarian.Cli` with persona claim, used by Cursor
  and Claude Desktop to inherit Engineering context for an
  engineer's session

## Evaluation

- **Per-persona golden set**: 100 Engineering Q&A pairs spanning
  code, runbooks, tickets, and SQL by end of Phase 2
- **Action-quality metrics** (Phase 4):
  - Agreement-with-human rate per action type
  - Override rate (how often a human re-classified after
    Recommend)
  - Time-to-resolution lift (Recommend mode vs. baseline)
- **Quality regressions** (Phase 4):
  - 7-day rolling agreement rate; auto-regress to a more cautious
    mode if it drops below 85% for any Autonomous action

## Pilot scope (v1, Phase 1 → Phase 4)

- **Phase 1**: Engineering persona membership for all Engineering
  contributors. Persona is selectable in the portal and the MCP
  CLI; retrieval profile and synthesis style apply.
- **Phase 2**: Engineering persona facets on wiki pages where the
  source pool warrants them; per-persona spot-check linter
  baseline.
- **Phase 3**: Code, SQL, and meeting-transcript sources land
  through the multimodal Skills; Engineering persona's source-type
  weights take effect.
- **Phase 4**: Action set above lands in **Recommend** mode;
  data accumulates toward Shadow promotion in v2.

## Sponsoring Persona Owner responsibilities

The Engineering Director (or designated equivalent) is named in
the persona row's metadata. The owner:

- Approves the action set above and any future additions
- Signs off on Recommend → Shadow promotions for this persona
- Reviews persona-quality dashboards weekly during the pilot,
  monthly after
- Owns reversibility-path realism review for each action before
  it promotes

## Open items for the v1 pilot

Tracked in [`../open-questions.md`](../open-questions.md) when
they ripen:

- Exact action-taxonomy alignment with the Engineering ticketing
  system (Jira / ADO / GitHub Issues)
- Whether `ticket.priority_suggest` should ever leave Recommend
  (deferred until Phase 4 data informs)
- Whether `code.suggest_owner` should be one persona action or
  two (separate "owner" vs. "reviewer" suggestions)

## See also

- [`../personas.md`](../personas.md) — Persona index
- [ADR 0014](../adr/0014-personas-first-class.md) — Personas as
  a first-class organizing concept
- [ADR 0015](../adr/0015-persona-aware-retrieval-synthesis.md) —
  Persona-aware retrieval and synthesis
- [ADR 0016](../adr/0016-persona-internal-autonomous-actions.md) —
  Internal autonomous actions, scoped per persona
- [`../decision-support-roadmap.md`](../decision-support-roadmap.md)
  — The persona rollout plan
