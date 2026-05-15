# ADR 0008 — Three deletion tiers (soft / hard / quarantine) with audit-event preservation

> Status: **Accepted** · Date: 2026-04-29 · Deciders: Architect (initial proposal — to be ratified)

## Context

A simplistic "hard cascade delete on every removal" model has three
problems at enterprise scale:

1. **Cost**: a popular source's deletion can trigger regeneration of
   dozens of wiki pages, each costing LLM calls. Routine librarian
   housekeeping shouldn't pay that price.
2. **Atomicity**: regenerating many pages in a single operation
   leaves the wiki in an inconsistent state mid-cascade.
3. **Conflict with audit retention**: SOX-style retention (7 years)
   says "keep the record of what happened"; GDPR-style RTBF says
   "remove the personal data". A blanket hard delete that scrubs the
   audit log violates one or the other.

Real RTBF requests are also frequently *concept-level* — "remove all
references to former employee X" — which spans many sources.

## Decision

We define **three deletion tiers** with distinct semantics, plus a
concept-level RTBF flow that selects between them per source.

### Tier 1 — Soft delete (default for librarian housekeeping)

Trigger: librarian removes a stale or superseded source through the
normal portal flow.

Effect:

- `sources.status` → `deleted`
- Source and its chunks are excluded from retrieval (vector and
  full-text search filter on `status`)
- Wiki claims citing the source's chunks are flagged
  `weakly_cited = true` via the linter on its next pass
- The blob in Azure Blob Storage is retained per the source's
  retention policy (default 7 years)
- Audit events untouched

Time to effect: immediate; visible to all queries within seconds.

Reversibility: `sources.status` → `approved` restores the source.

### Tier 2 — Hard delete (RTBF and Legal/HR-initiated)

Trigger: a librarian or admin issues a hard delete; or the concept-
level RTBF flow selects this tier; or a Legal Hold expires and
disposition is "purge".

Effect:

1. `sources.status` → `deleted_hard`; access revoked
2. Audit event `source.hard_deleted` written with full context
3. Cascade-Regeneration Worker enqueues a job per affected wiki page
4. Worker processes each page atomically: rebuilds **each affected
   facet** of the page (per ADR 0006 — a single page may carry an
   `Internal` facet, a `Confidential` facet, and so on; deleting a
   `Confidential` source may invalidate claims in the `Confidential`
   facet without affecting the `Internal` facet, while deleting an
   `Internal` source potentially invalidates claims in every facet
   that uses it). For each affected facet, the worker builds the new
   revision from the remaining sources visible at that facet's
   classification, validates citations, commits the new revision,
   releases the old claims for FK cascade
5. Chunks and embeddings are FK-cascaded and deleted; any
   `source_shares` rows for the deleted source are FK-cascaded
6. Blob is purged after a **default 1-hour delay** during which a
   Legal user can attach a Legal Hold and stop the purge. Immutability
   override is permitted only with a `LegalDelete: true` tag; a
   pre-existing Legal Hold blocks the purge entirely until lifted.
   The 1-hour window is configurable per department.
7. Existing audit events that *reference* the deleted source are
   **tombstoned**: their event metadata is preserved (who, when,
   action, dept) but content references (titles, snippets, prompts)
   are scrubbed via a controlled rewrite

Time to effect: cascade is async; affected pages may show a "this
page is being updated" placeholder for up to 5 minutes (configurable
SLA in Phase 4).

Reversibility: **none** for content; audit metadata remains.

### Tier 3 — Quarantine (Legal Hold or under-investigation)

Trigger: source flagged sensitive, contested, or under legal review.

Effect:

- `sources.status` → `quarantined`; removed from active retrieval
- Chunks and embeddings remain in the database but are filtered out
- Blob remains in cold/WORM tier
- Wiki claims citing the source's chunks are flagged for the linter
  but **not** regenerated until Legal disposes of the source
- Audit events untouched

Time to effect: immediate.

Reversibility: status → `approved` restores; status → `deleted_hard`
applies the hard-delete cascade.

### Concept-level RTBF flow

For "purge everything related to X":

1. A user with `Admin` role (typically a Legal liaison) opens the
   concept-RTBF tool in the portal and supplies the concept (an
   entity name, a person's identifying tokens, etc.)
2. The system runs a hybrid search (vector + entity + full-text)
   across the corpus and presents the candidate matches grouped by
   source
3. The Legal user reviews the candidate set with a librarian and
   selects a tier (hard delete vs. quarantine) per source
4. The selected tier is applied to each source through the standard
   flow above
5. A single `concept_rtbf` audit event records the request, the
   reviewers, the candidate set, and the selected dispositions

This is **always** human-in-the-loop. Automatic concept-level deletion
is too easy to over-purge.

### Cascade budgeting

The Cascade-Regeneration Worker enforces:

- A per-job page-count cap (default 50; admin-overridable)
- A per-tenant token budget (configurable; alert at 80%, halt at 100%)
- A maximum end-to-end SLA (default 15 minutes; alert if exceeded)

If a single cascade exceeds the cap, the worker schedules continuation
jobs and sends a librarian notification.

### Audit-event tombstoning

The audit ledger does **not** delete event rows on hard delete.
Instead, a tombstoning routine rewrites the `details` JSONB column to
preserve event metadata and remove content references:

```jsonc
// Before
{
	"event": "source.approved",
	"source_id": "abc-123",
	"title": "Customer X — confidential strategy doc",
	"uploader": "alice@contoso.com",
	"prompt_excerpt": "..."
}

// After tombstoning
{
	"event": "source.approved",
	"source_id": "abc-123",
	"title": "[REDACTED:RTBF]",
	"uploader": "alice@contoso.com",
	"prompt_excerpt": "[REDACTED:RTBF]",
	"tombstoned_at": "2026-09-15T14:00:00Z",
	"tombstoned_under": "Legal-Request-2026-Q3-014"
}
```

This preserves the *event* (who did what, when, in which department)
while removing the personal/sensitive *content*. We believe this is
the standard reconciliation between SOX-style retention and GDPR
Article 17, but Q1 in `open-questions.md` requires Legal sign-off.

## Consequences

### Easier

- Routine librarian housekeeping (soft delete) is cheap and fast.
- Real RTBF requests have a deterministic, auditable path.
- Legal Hold is a first-class state, not a workaround.
- Concept-level requests get human review before they cause damage.

### Harder

- Three deletion paths instead of one. The portal UX must make the
  distinction obvious.
- Tombstoning logic adds complexity to the audit ledger.
- Cascade-Regeneration Worker is a non-trivial component to build,
  test, and operate.

### Risks

- A bug in tombstoning leaves content references in audit events.
  Mitigation: dedicated tests + a periodic "RTBF residue" lint job
  that scans audit content for references to deleted source IDs.
- Cascade overruns budget caps and either halts or runs partial.
  Mitigation: the worker is restartable; partial cascades are visible
  in the librarian portal.

## Alternatives considered

### Single-tier hard cascade only

Original plan; rejected because routine housekeeping shouldn't trigger
the expensive cascade path, and Legal Hold has no natural place.

### Hard delete with full audit-event removal

Violates SOX-style retention. Rejected.

### Soft delete only; never hard delete

Doesn't satisfy RTBF. Rejected.

### Manual tombstoning per request

Possible but error-prone at scale. The automated routine is worth the
complexity.

## References

- ADR 0006 — LLM-only wiki authoring (the substrate that makes
  cascade regeneration tractable)
- ADR 0007 — Claim-level citation contract (the structural enabler)
- ADR 0010 — Audit ledger
- Open question Q1 — audit retention period
- Open question Q2 — RTBF concept-level scope
- [GDPR Article 17 — Right to erasure](https://gdpr-info.eu/art-17-gdpr/)
