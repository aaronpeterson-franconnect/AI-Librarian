# ADR 0010 — Single append-only audit ledger in Postgres, partitioned monthly, exported to SIEM

> Status: **Accepted (amended 2026-04-30)** · Date: 2026-04-29 · Deciders: Architect (initial proposal — to be ratified)

## Amendment note

Amended on 2026-04-30 in lockstep with
[ADR 0014](0014-personas-first-class.md),
[ADR 0015](0015-persona-aware-retrieval-synthesis.md), and
[ADR 0016](0016-persona-internal-autonomous-actions.md):

- The event taxonomy gains two new families: `persona_action.*`
  and `persona_membership.*`, covering the recommend/shadow/
  autonomous lifecycle plus persona-membership grants and
  revocations.
- The `query.*` family carries persona context (the persona under
  which a retrieval or synthesis event ran) in its `details` JSONB.
- The list of belt-and-braces DB triggers gains
  `persona_action_records`, `persona_action_outcomes`,
  `persona_memberships`, and `personas`.

Persona is recorded as **metadata only**, consistent with the
content-capture policy below. The persona ID and the persona's
`default_action_set` reference are captured; persona names and
descriptions are not duplicated into every event.

## Context

Auditing was selected as a non-negotiable scope item. We need a record
of every action that meets the standard expected for SOC2 / SOX-style
controls and supports the tombstoning requirement of RTBF (ADR 0008).

We also need observability into LLM cost: per-department, per-purpose
(ingest, lint, ask), per-model. Synthadoc pioneered the pattern of
recording token counts and cost estimates inline with the audit
record; we adopt it.

## Decision

A single Postgres table, `audit_events`, captures every meaningful
action across the system. The table is **append-only** — there is no
public-facing UPDATE or DELETE path; the only "modification" allowed
is the controlled tombstoning routine for RTBF, which itself records
a tombstone metadata blob.

### Schema

```sql
CREATE TABLE audit_events (
	id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
	occurred_at     timestamptz NOT NULL DEFAULT now(),
	actor_user_id   uuid        NOT NULL,            -- 'system' for agent actions
	actor_role      text,                             -- effective role at action time
	originated_by   uuid,                             -- the human user who triggered an agent chain
	department_id   uuid        REFERENCES departments(id),
	event_type      text        NOT NULL,
	event_subtype   text,
	target_kind     text,                             -- e.g. 'source', 'wiki_page', 'policy'
	target_id       uuid,
	correlation_id  uuid        NOT NULL,             -- ties multi-step flows together
	llm_provider    text,
	llm_model       text,
	llm_prompt_tokens     int,
	llm_completion_tokens int,
	llm_cost_estimate_usd numeric(10,6),
	llm_latency_ms        int,
	outcome         text        NOT NULL,             -- 'success' | 'failure' | 'partial'
	error_class     text,
	details         jsonb       NOT NULL DEFAULT '{}',
	tombstoned_at   timestamptz,
	tombstoned_under text                              -- legal request reference
)
PARTITION BY RANGE (occurred_at);
```

Monthly partitions are created by a scheduled job. Old partitions move
to a cheaper storage class on a configurable schedule and are
ultimately retained according to the audit retention policy
(open question Q1).

### Indexing

- `occurred_at, department_id, event_type` — operational queries
- `actor_user_id, occurred_at` — user-history queries
- `correlation_id` — flow reconstruction
- `target_kind, target_id, occurred_at` — "show me everything that
  happened to this source"
- GIN on `details` for ad-hoc filtering

### Event taxonomy

The `event_type` and `event_subtype` form a controlled vocabulary
(documented in `docs/audit-events.md` once Phase 0 lands). Major
families:

| Family | Examples |
|---|---|
| `source.*` | submitted, approved, rejected, soft_deleted, hard_deleted, quarantined, classification_changed, shared, share_revoked, share_expired |
| `wiki.*` | revision_committed, claim_added, claim_invalidated, page_locked, page_unlocked, proposal_filed, proposal_approved, facet_added, facet_removed |
| `policy.*` | created, updated, deactivated |
| `directive.*` | created, updated, expired, deleted, conflict |
| `permission.*` | granted, revoked, group_synced |
| `query.*` | mcp_search, mcp_ask, portal_search, portal_ask |
| `ingest.*` | started, finished, failed, skill_dispatched, skill_completed |
| `lint.*` | started, finding, finished |
| `cascade.*` | started, page_regenerated, finished, halted_budget |
| `rtbf.*` | concept_request, candidate_set_built, dispositions_applied, tombstone_completed |
| `auth.*` | sign_in, sign_out, token_refreshed, group_sync |
| `mcp.*` | session_opened, session_closed, client_identified, client_outside_approved_list |
| `audit.startup.*` | providers_configured, provider_tier_unverified |
| `persona_action.*` | proposed, shadowed, committed, reversed, outcome_recorded, mode_changed |
| `persona_membership.*` | granted, revoked, expired |

### Capture mechanism

Audit writes happen through a single `IAuditWriter` injected
everywhere relevant:

- ASP.NET Core middleware writes auth and request-level events
- The LLM Gateway (ADR 0003) writes one event per LLM call with
  token / cost / latency
- Ingestion workers and librarian agents write events at each major
  step
- Database triggers on `sources`, `source_shares`, `page_facets`,
  `wiki_pages`, `wiki_page_revisions`, `wiki_claims`,
  `department_policies`, `department_directives`, `personas`,
  `persona_memberships`, `persona_action_records`, and
  `persona_action_outcomes` act as belt-and-braces capture for
  direct DB activity (which we do not expect in normal operation
  but want to detect if it occurs)

### Content-capture policy

By default, audit events capture **metadata only** for LLM
interactions: caller, role, department, persona, model, token
counts, cost, latency, retrieved-page IDs, outcome. Prompt and
completion **text** are not stored in the ledger. Persona is
captured as the persona ID; the persona's `retrieval_profile`
and `synthesis_style` are not duplicated per event (they are
versioned on the `personas` row and joined when needed).

This is a deliberate choice (per ADR 0004): cleaner privacy posture,
smaller sensitive-data surface, simpler RTBF tombstoning. It does
mean some "what did the model say last Tuesday" forensics requires
the user's help.

The metadata-only choice also aligns with **ADR 0012** (enterprise-
tier LLM access): neither the LLM provider nor the audit ledger
retains the sensitive content of any individual conversation. The
two protections compound — together they put the company in a
defensible position for any "where did this content go?" inquiry.

Diagnostic content capture is an **opt-in** mode that can be enabled
per-department or per-user, time-bounded, and visible to everyone in
scope. Enabling it is itself an audited event (`audit.diagnostic.enabled`).

All audit writes are best-effort with **non-blocking failure**: if the
audit table is unreachable, the originating action fails closed (we
prefer "no action" to "unaudited action"). The exception is
read-only queries, which can fail-open with an alert.

### SIEM export

A Logic App or Container Apps Job streams new partitions to Microsoft
Sentinel via the Log Analytics ingestion API. From Sentinel, security
rules can fire on suspicious patterns (e.g., a librarian hard-deleting
50 sources in 5 minutes).

### Retention and tombstoning

- Default retention: 7 years of full content; metadata-only beyond
  that, indefinitely
- Tombstoning per ADR 0008: rewrites `details` to scrub content
  references but preserves the event's existence

The exact retention policy is TBD (open question Q1) and requires
Legal sign-off before Phase 0 ships.

## Consequences

### Easier

- One place to look for "what happened" — per user, per source, per
  department, per correlation flow.
- Cost analysis is a SQL query (sum tokens by department, by month).
- SIEM integration is an export, not a redesign.
- Tombstoning targets a single column shape.

### Harder

- Append-only at scale demands partition management; we automate
  partition creation and aging.
- A high-volume system fills the audit table fast. Mitigation:
  partitioning, columnstore considered for archive partitions in
  Phase 5.
- Every service has to remember to call the writer. Mitigation:
  middleware + DB triggers cover the common cases; lint catches the
  rest.

### Risks

- Audit volume swamps the OLTP workload. Mitigation: separate
  tablespace, monitor, and consider a separate Postgres instance for
  audit at Phase 4 if needed.
- An incomplete tombstoning leaves residue. Mitigation: a periodic
  "RTBF residue" lint that scans `details` for known-deleted IDs
  (ADR 0008).

## Alternatives considered

### Append-only event store (Kafka, Event Hubs)

Better for streaming and analytics, but worse for transactional
guarantees with relational data and adds another runtime. Postgres
suffices at our scale.

### One audit table per domain

Fragments the picture and complicates SIEM export. Rejected.

### Audit only LLM calls

Insufficient. Permission changes, source approvals, and deletions all
need first-class audit.

## References

- ADR 0003 — LLM Gateway (per-call telemetry feeds the ledger)
- ADR 0008 — Tiered deletion and RTBF (defines tombstoning)
- [ADR 0014](0014-personas-first-class.md) — Personas as a
  first-class organizing concept (the source for
  `persona_membership.*` events)
- [ADR 0015](0015-persona-aware-retrieval-synthesis.md) — Persona-
  aware retrieval (persona context on `query.*` events)
- [ADR 0016](0016-persona-internal-autonomous-actions.md) —
  Internal autonomous actions (the source for `persona_action.*`
  events)
- [Synthadoc audit-trail design](https://github.com/axoviq-ai/synthadoc)
- Open question Q1 — audit retention period
