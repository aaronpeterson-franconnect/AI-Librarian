# Legal & Compliance Brief — AI Librarian

> Audience: Legal Counsel, Compliance Officer, DPO if applicable
> Status: **Draft for Legal review** · Date: 2026-04-29
> Companion documents: [Architecture](../architecture.md) · [Executive summary](../executive-summary.md)

## Why we need your input

AI Librarian is a planned enterprise knowledge platform. Each
department curates a corpus of documents, code, recordings, and other
artifacts; an LLM continuously distills that corpus into a living
internal wiki; everyone in the company can ask questions through the
AI tools they already use (Cursor, Copilot, Claude, ChatGPT, Teams)
and get answers grounded in our own documents.

**Access model in one paragraph** (so the decisions below have the
right context): every source is labelled with one of four
classifications — `Public`, `Internal`, `Confidential`, `Restricted`.
Classification is the default access boundary. `Internal` content
(the default) is readable by any authenticated employee company-wide,
which makes cross-department collaboration the default for routine
knowledge. `Confidential` and `Restricted` content stays scoped to
the owning department, with explicit auditable shares for
exceptions. **Write authority** (submit, approve, govern, delete)
remains strictly department + role gated regardless of who can read.
B2B guests are excluded from the company-wide `Internal` read tier;
they only see what they're explicitly granted. Every LLM endpoint
the system touches operates under an enterprise-tier agreement with
no-training and bounded-retention guarantees (per ADR 0012). See
[ADR 0011](../adr/0011-data-classification.md) and
[ADR 0005](../adr/0005-rls-with-entra.md) for the full model.

The architecture has been designed with auditability,
right-to-be-forgotten, and access control as first-class concerns —
not afterthoughts. **Three decisions remain that we cannot make
without Legal sign-off**, and we need you to anchor them before we
ship Phase 0 (foundational data model). All three are summarized
below with our proposed position.

If you accept our proposals as-stated, we can proceed without
modification. If you would adjust any of them, the architecture is
designed to accommodate within reasonable limits — we just need
your direction.

---

## Decision 1 — Audit retention period

### What the system does

Every meaningful action in AI Librarian is recorded in an
append-only audit ledger:

- **Operational events** — sign-in, search, query, ingest start/finish
- **Content events** — source approved, rejected, soft-deleted, edited
- **Governance events** — hard-delete, permission grant, policy change,
  concept-level RTBF, page lock/unlock
- **LLM-call events** — model used, token counts, cost estimate,
  retrieved-page IDs (but **not** prompt or response content; that's
  a deliberate privacy choice — see ADR 0010)

Each event row includes metadata (who, when, action, department,
target IDs) and a `details` JSONB blob with content references
(titles, snippets, IDs of affected sources).

### Our proposal

| Tier | What | Default retention |
|---|---|---|
| 1 | Full event including `details` content references | **7 years** |
| 2 | After 7 years, scrub `details` to remove content references; preserve event metadata | **Indefinite** |
| 3 | RTBF-triggered scrub | **Immediate** — content references scrubbed when source is hard-deleted, regardless of age |

**Why 7 years**: matches SOX retention, the longest common enterprise
standard, and captures the period during which most regulatory
inquiries arise. Conservative default we can adjust downward if our
specific regulatory exposure is lighter, or upward for specific event
types.

**Why indefinite metadata**: event metadata is small (a few hundred
bytes per row), enormously valuable for forensic and trend analysis,
and contains no personal data beyond user IDs (which are themselves
tied to the enterprise identity system).

**Why immediate RTBF scrub**: GDPR Article 17 is satisfied;
non-RTBF retention is unaffected.

### What we need from you

1. **Confirm 7 years is correct for our regulatory exposure**, or
   specify the right number. Sector-specific overrides we should
   support? (E.g., longer for Finance / HR? Shorter for marketing
   research?)
2. **Confirm immediate RTBF scrub is acceptable** without waiting
   for the natural retention period to end.
3. **Confirm indefinite metadata retention is acceptable** — i.e.,
   we don't have a "former employee data must be fully purged after
   N years" obligation that conflicts with retaining their user ID
   in audit metadata.
4. **Identify any per-action retention overrides** we must support
   (e.g., permission-grant events retained 10 years rather than 7).

### Phase relevance

**Phase 0 blocker** — we need this resolved before the audit ledger
schema is finalized.

---

## Decision 2 — Right-to-be-forgotten: concept-level scope

### What the system does

We support three deletion tiers (ADR 0008):

- **Soft delete** — librarian housekeeping; reversible; audit
  unchanged
- **Hard delete** — permanent; cascade-regenerates affected wiki
  pages; audit events tombstoned (metadata kept, content scrubbed)
- **Quarantine** — sensitive or under-investigation; removed from
  retrieval but retained for legal hold

For "purge anything related to person/topic X" requests (e.g.,
former employee, customer relationship ended, acquisition target
fell through), we have a separate **concept-level RTBF flow**:

1. An Admin opens the tool and supplies the concept (an entity name,
   identifying tokens)
2. The system runs a hybrid search across the corpus and presents
   candidate matches grouped by source
3. Reviewers select a tier (hard delete vs. quarantine) per source
4. A single `concept_rtbf` audit event records the request,
   reviewers, candidate set, and dispositions

The flow is always human-in-the-loop. Automatic concept-level
deletion is too easy to over-purge.

### Our proposal

**Three valid reasons for a concept-level RTBF request**:

1. **Person data**: employees who have left, customers, third parties
   whose personal data must be removed under GDPR Article 17 or a
   contractual obligation. **Approval**: Legal alone.
2. **Customer or counterparty cleanup**: when a customer relationship
   ends and contractual cleanup is required. **Approval**: Legal + the
   account-owning executive.
3. **Project / acquisition codename cleanup**: when a confidential
   project, acquisition target, or strategic initiative is no longer
   relevant or must be cleansed. **Approval**: Legal + executive
   sponsor.

**Not a valid reason**: "we don't want this anymore" / generic
housekeeping. Use normal source-by-source soft delete for those.

**Documentation requirement**: every concept-RTBF request carries a
reference number (e.g., `Legal-Request-2026-Q3-014`), the requesting
party, the justification, and the reviewing parties' identities.
This metadata is preserved in the `concept_rtbf` audit event
indefinitely (the action of having done it is itself the record).

### What we need from you

1. **Confirm the three valid reasons** are correct, or amend the
   list.
2. **Confirm the approval thresholds** (Legal alone vs. Legal +
   executive). Does any reason require board-level sign-off?
3. **Confirm the documentation requirement** is sufficient for
   audit defensibility.
4. **Specify the residue handling**: when a concept-RTBF tombstone
   itself contains content references (e.g., the legal request
   mentions the person's name), do those references also get scrubbed
   on a separate timeline?

### Phase relevance

**Phase 4** — the concept-RTBF tool ships in Phase 4. We have time
to refine the policy, but the framework above is what the system
will be built to support.

---

## Decision 3 — Cross-border data residency

### What the system does

AI Librarian is deployed to Microsoft Azure. Azure resources are
regional. Postgres, Blob Storage, Container Apps, and Azure OpenAI
all live in a chosen Azure region.

The system can in principle support per-department region binding
(department X's sources, embeddings, and wiki content live in
region Y) — but doing so adds significant complexity to the data
model, RLS policies, and operational overhead.

### Our proposal

**For v1**: deploy to a single Azure region — **East US 2**
(or whichever region IT prefers within the United States). All
data lives there; no per-department region binding.

**Rationale**:

- We are a US-headquartered organization
- Most regulatory regimes (US, Canada, most non-EU) are satisfied
  by single-region deployment with appropriate disclosure
- Multi-region deployment would add complexity disproportionate to
  v1 needs
- Phase 5+ can introduce per-department region binding if a future
  requirement forces it (e.g., an EU subsidiary demanding EU-region
  deployment under GDPR data-locality interpretations)

### What we need from you

1. **Confirm US single-region is acceptable** for our current data
   subject mix.
2. **Confirm no department today has a EU-only or specific-region
   data residency requirement** (e.g., Privacy Shield successor
   commitments, sector-specific data localization).
3. **Identify any planned acquisitions, partnerships, or expansions**
   in the next 18 months that would change this — so we know whether
   to design Phase 5 region-binding capability sooner.
4. **Specify any data subject notice obligations**: do we need to
   update the privacy policy? Notify EU data subjects whose content
   may be ingested?

### Phase relevance

**Phase 0 blocker** — we cannot pick the Azure region without this
direction.

---

## Summary — what we are asking Legal to do

1. Review the three proposed positions above.
2. Sign off, amend, or reject each.
3. Specifically confirm:
   - Audit retention period (default 7 years content / indefinite metadata)
   - Concept-RTBF approval thresholds (Legal alone for person data; Legal + exec for customer/project)
   - Single US-region deployment (East US 2 default)
4. Identify any **additional Legal/Compliance constraints** the
   architecture should know about that aren't covered by the three
   questions above.

If you accept all three proposals as-stated, no architecture changes
are required and Phase 0 can begin. Adjustments are accommodated by
configuration changes, not architectural rework.

## Where to read more

- [`../architecture.md`](../architecture.md) — full system blueprint
- [`../adr/0008-tiered-deletion-and-rtbf.md`](../adr/0008-tiered-deletion-and-rtbf.md) — deletion model
- [`../adr/0010-audit-ledger.md`](../adr/0010-audit-ledger.md) — audit ledger
- [`../adr/0011-data-classification.md`](../adr/0011-data-classification.md) — classification model
- [`../open-questions.md`](../open-questions.md) — full open-questions tracker
