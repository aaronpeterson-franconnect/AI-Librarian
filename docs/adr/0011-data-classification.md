# ADR 0011 — Data classification as the default access boundary

> Status: **Accepted (amended 2026-04-29)** · Date: 2026-04-29 · Deciders: Architect

## Amendment note

This ADR was originally accepted on 2026-04-29 with the framing
"classification is a label, not an access boundary." Later the same
day, the Architect surfaced a real concern: that framing combined with
strict per-department RLS made the system too isolated to support
natural cross-department collaboration (an Engineer reading
Marketing's positioning doc, a new hire in Sales reading Engineering's
release process, etc.).

This amendment **flips classification to the default access boundary**,
preserving the prior structure (four tiers, defaults, retention,
audit, UI) but using classification + department membership together
to gate read access. Department membership remains the only gate for
*write* access.

The prior version is retained in git history; the architecture has
not yet shipped, so no migration is required.

## Context

Enterprise content has heterogeneous sensitivity. Most departmental
knowledge — coding standards, vendor-evaluation playbooks, release
runbooks, customer onboarding processes, marketing positioning,
operational metrics — should be readable across departments. A small
fraction is genuinely sensitive (HR personnel files, M&A
discussions, customer PII, security incidents) and must be scoped
strictly.

We had two design directions:

1. **Department-private by default, with explicit cross-department
   sharing** — strict isolation; collaboration is the exception.
2. **Internal-by-default, with explicit per-source restriction** —
   open by default; restriction is the exception.

(1) defeats the platform's purpose. People will route around it
(forwarded emails, side channels, "I'll just paste it for you")
because the system fights how organizations actually work. (2)
matches reality: most knowledge wants to be read; a known minority
must be guarded.

## Decision

**Classification is the default access boundary.** Combined with
department membership for protective tiers and explicit shares for
exceptions, classification controls who can read which sources.
Department membership remains the only gate for who can write,
review, govern, or delete.

### The ladder

Four tiers, ordered:

| Tier | Default visibility | Who can read |
|---|---|---|
| `Public` | World | Anyone (rare; explicit librarian action) |
| `Internal` | All employees | Any authenticated **employee** (tenant member). B2B guests are excluded — they fall back to strict department membership and explicit shares |
| `Confidential` | Department-scoped | Members of the owning department, or a department the source has been explicitly shared with |
| `Restricted` | Department + role-scoped | Members of the owning department who hold `Librarian` or `Admin`, plus explicit shares |

**Note on B2B guests**: a tenant Entra account is not the same as
"employee." B2B guests authenticate against our tenant (via Entra
B2B) but are not full employees. The system distinguishes the two
via the `app.is_employee` session variable; the `Internal`
company-wide read access requires `is_employee = true`. This
preserves the IT brief's Pattern B (B2B guest scoped to a single
department's `Reader` group) under the classification-driven model.
See ADR 0005 for the predicate.

The ladder is system-wide and version-stable. New tiers cannot be
added without an ADR amendment.

### Where classification is set

- **System default**: `Internal`. New sources without a more-specific
  signal are Internal — readable across the company.
- **Per-department default**: a department's `policy.yaml` may set
  a different default (e.g., HR's policy sets `default: Confidential`).
  Applied to ingest from that department when no override is supplied.
- **Per-source override**: contributors can set classification at
  submission time; librarians can change it at any time.
- **Skill-suggested**: a Skill can recommend a classification based
  on content signals (PII detection, financial keywords, document
  watermarks). Surfaced to the approver as a recommendation; never
  auto-applied above the policy default.

### Source shares — explicit cross-department exposure

A `source_shares` table allows a Librarian or Admin to grant a
specific other department read access to an individual Confidential
or Restricted source:

```
source_shares (
  source_id,
  shared_with_department_id,
  granted_by_user_id,
  granted_at,
  expires_at,        -- optional; defaults to NULL (indefinite)
  reason,            -- short text; required
  audit_event_id     -- references audit_events
)
```

**Use cases**:
- Finance shares the FY27 budget doc with Engineering for project Atlas
- HR shares a policy update with all department Librarians for review
- Legal shares a contract template with Sales

**Constraints**:
- Only Librarian (of the owning department) or Admin may grant
- Always recorded as an `audit_events` row of type `source.shared`
- Expiration optional; the granting Librarian decides
- Revocable at any time by the granting Librarian or Admin
- Restricted sources require Admin grant (Librarian alone insufficient)

A share gives the target department's members **read** access to
the specific source. It does not grant write, edit, or delete rights
to anyone outside the owning department.

### What classification gates (read side)

- **Read access**: enforced by RLS predicates that combine
  classification, department membership, and `source_shares` (see
  ADR 0005 for the SQL).
- **Editorial filter / approval queue**: the department policy
  declares `require_approval_for: [classification: Confidential,
  classification: Restricted]`. Sources at or above the threshold
  always go to the librarian queue regardless of contributor trust.
- **Retention**: tier-specific defaults override the department
  default unless explicitly set:
	- `Public`: 3 years
	- `Internal`: 7 years
	- `Confidential`: 7 years (with Legal review at year 5)
	- `Restricted`: 10 years (Legal Hold likely)
- **UI**: the portal renders a classification badge on every source
  and on every wiki page that cites a source at or above
  `Confidential`. Users see at a glance what they are reading.
- **Audit**: every audit event records the classification of the
  affected source. Filtering "show me all access to Restricted
  sources in the last 30 days" is a single SQL query.
- **Retrieval ranking**: at parity, lower-classification sources
  rank above higher when both are valid answers; this nudges the
  system toward less-sensitive citations when alternatives exist.

### What classification does NOT gate

- **Write access**: gated strictly by department + role membership
  (Contributor / Reviewer / Librarian / Admin). Classification does
  not change who may submit, approve, govern, or delete a source.
- **Audit visibility for Admins**: Admins always see the full audit
  ledger regardless of source classification.

### Wiki-page facets

Cross-department reads complicate the wiki: a single page about
"Customer onboarding" might cite both Internal sources (the general
process, team responsibilities) and Confidential sources (specific
dollar thresholds, sensitive customer SLAs).

If we synthesize one page from mixed sources and gate it at the
most-restrictive level, every employee outside Sales loses access to
the public process information. If we gate it at the least-restrictive
level, we leak Confidential content to everyone. Both are wrong.

**Solution**: the wiki page model supports **facets**.

- A page is a topic + slug (e.g., `/wiki/customer-onboarding`)
- A page has 1+ facets, each with a content body and a minimum
  classification required to read
- The Wiki Maintainer produces one facet per visibility tier when
  available sources support it:
	- An **Internal facet** synthesized from Internal-and-below
	  sources only
	- A **Confidential facet** synthesized from all available
	  sources visible to the department
	- A **Restricted facet** synthesized when Restricted sources
	  exist and would add information beyond the Confidential facet
- `read_page(slug)` returns the highest-classification facet the
  caller can access — i.e., the most detailed page available to
  them
- A reader who can access only the Internal facet sees a less-detailed
  page but is not told what they're missing beyond a small
  "additional content available to {Department} members" note

This preserves the broadest possible knowledge sharing while keeping
sensitive material strictly gated. Citations within a facet only
reference sources visible at that facet's classification level — a
Confidential source never appears as a citation in the Internal facet.

See ADR 0006 for the synthesis logic and ADR 0007 for the citation
implications.

## Consequences

### Easier

- The system matches how organizations actually work: most knowledge
  is shared; a known subset is protected.
- Adopting AI Librarian is friction-free for new departments — they
  ingest content and it's immediately readable across the company.
  Hardening (Confidential / Restricted) is opt-in, not opt-out.
- Cross-department collaboration is a first-class scenario, not an
  exception requiring workarounds.
- The classification ladder, retention rules, audit fields, and UI
  badges from the prior version of this ADR all transfer unchanged.

### Harder

- The wiki synthesis pipeline is more complex (page facets) than
  one-page-per-topic.
- Librarians have a real responsibility: misclassifying a source as
  `Internal` when it should be `Confidential` exposes it
  company-wide. Mitigation: Skill-suggested classifications,
  approval queue review, audit alerts when a low-classification
  source contains PII patterns post-ingest.
- The RLS predicates are slightly more complex (classification +
  department + share-table lookup vs. just department).

### Risks

- **Misclassification at ingest**: a Contributor uploads sensitive
  content with default `Internal` and it's exposed company-wide
  before a Librarian reviews. Mitigation: department policy can set
  `default: Confidential` to invert the default for sensitive
  departments; PII-detection Skill flags suspect content
  pre-approval; sources start in approval queue if the policy
  requires it; Librarians can downgrade visibility (broaden) or
  upgrade visibility (narrow) at any time, and any change is audited.
- **Share-grant abuse**: a Librarian shares many Confidential
  sources to many departments, defeating the access model.
  Mitigation: share grants are individually audited; a periodic
  sentinel rule flags Librarians whose share-grant rate is anomalous.
- **Facet drift**: the Internal facet of a page could become stale
  while the Confidential facet evolves. Mitigation: facets are
  always regenerated together when source pool changes; they
  cannot drift independently.

## Future enhancements (deferred)

The v1 model is intentionally coarse: four classification tiers,
department-scoped shares, page-level facets. Real enterprise life
will eventually want finer control. Each of the following extensions
is **additive to the v1 schema** — none requires a rewrite of
existing tables, RLS predicates, or session-variable plumbing.
Captured here so we don't lose the thread; tracked for prioritization
in [`../future-enhancements.md`](../future-enhancements.md).

### Read-side finer control

| Enhancement | Trigger scenario | Upgrade path |
|---|---|---|
| **Per-user source share** | A Librarian needs to share a single source with one specific external auditor, not the auditor's whole department | Add `shared_with_user_id` to `source_shares` (or a peer table); RLS gains an `OR EXISTS user-share` branch |
| **Group-based source share** | Share a Legal-template source to "All Librarians company-wide" so any department's librarian can reference it | Discriminated grant target on `source_shares` (`dept` / `user` / `group`); group-membership lookup in the predicate |
| **Project / initiative scope** | A cross-functional "Project Atlas" team spans Engineering + Finance + Product; sources tagged to the project should be visible to project members regardless of department | New `projects` + `source_projects` tables; new `app.project_ids` session variable; additive RLS branch |
| **Sub-classifications** | We want to handle `Confidential-PII` differently from `Confidential-Financial` for retention, audit, and reviewer routing | Move `classification` to a lookup table or expand the CHECK constraint; predicate complexity grows linearly |
| **Chunk-level classification** | A 200-page PDF where Appendix B contains PII but the rest is general process documentation; we want different gating per appendix | Add `classification` to `chunks` defaulting to parent source's value; the chunks-RLS join already exists |
| **Claim-level visibility within a facet** | A Confidential facet contains a few claims that should be Restricted-only even within the owning department | Add `min_classification` to `wiki_claims`; rendering pipeline filters claims at read time within an otherwise-readable facet |
| **Need-to-know acknowledgment** | A Restricted source requires the reader to click-through an NDA-style acknowledgment before first read | New `source_acknowledgments` table; predicate gains an additional check for the Restricted tier |
| **Time-bounded user grants** | Auditor needs read access to a project's sources for the duration of the engagement (90 days) | Already supported via `source_shares.expires_at` — just needs the per-user share variant |

### Authority-side finer control

| Enhancement | Trigger scenario | Upgrade path |
|---|---|---|
| **Cross-department reviewer pools** | A Legal-reviewer queue any Legal-reviewer can pick up regardless of which department's source it is | Add a shared-queue concept; reviewer-pool table; queue-routing logic in the API |
| **Department sub-structures** | "Backend within Engineering" becomes a real concept needing inherited access | Reintroduce optional hierarchy via `ltree` path on `departments` (the v0 design rejected for v1 simplicity); RLS gains a path-overlap check |
| **ABAC attributes** | Gating on user attributes beyond department/role: "manager-only", "tenure > 2 years", "PCI-trained" | New attribute dimensions on `users`; extend session variables; new RLS predicates evaluated against attributes |

### Process-side finer control

| Enhancement | Trigger scenario | Upgrade path |
|---|---|---|
| **Auto-reclassification** | Sources age and become less sensitive; or content drift changes their effective classification (a Confidential project becomes public after launch) | Background job that re-evaluates classification on a schedule; `classification_history` audit trail; existing column unchanged |
| **External / B2B shares** | Contractors, customers, or partners need scoped access to specific sources | Already deferred to Phase 5+ via Q7 (B2B guests as Readers). Per-source guest shares would extend `source_shares` with a B2B-guest grant target |
| **Per-source watermarking** | Forensic tracking of which user accessed which bytes of a Restricted source | New `read_audit` table at byte/page granularity; client-rendered watermark overlay. Heavy; only if compliance demands it |

### Forward-compatibility audit

Each item above is supported by a specific v1 design element:

| v1 element | What it enables for the future |
|---|---|
| `source_shares` is its own table, not a column on `sources` | Per-user shares, group shares, project shares all add columns or peer tables without disturbing the source row |
| `classification` is a single column with CHECK | Sub-classifications add to the CHECK or migrate to a lookup table; both paths are clean |
| Session variables (`app.department_ids`, `app.contributor_depts`, etc.) | New dimensions (`app.project_ids`, `app.attributes`) plug in alongside existing ones |
| RLS predicates are additive `OR` clauses | Every new control axis adds a clause; nothing existing has to change |
| Page facets keyed by `min_classification` | Generalizes to "min audience" — facets could be keyed by project, attribute, or sub-classification later |
| The two-axis design (read vs. write) | Read-side finer control doesn't disturb write-side authority, and vice versa |
| Audit ledger is event-based with JSONB details | New event types (`share.user.granted`, `project.scope.added`, `acknowledgment.required`) drop in without schema changes |

The cost of adding any one item later is bounded: a new table or column, a new RLS clause, a new session variable, possibly a new audit event type. None of them require revisiting v1's core decisions.

## Alternatives considered

### Department-private by default

The original v0 of this ADR. Rejected because it fights how
organizations actually work and forces side-channel knowledge
transfer that defeats the platform's purpose.

### Classification as a pure auth dimension (Reader-Internal vs Reader-Confidential)

Tried in early sketches. Doubles the Entra group count, complicates
RLS, and creates combinatorial blowup if more tiers are ever added.
Rejected — the classification ladder is a property of the *content*,
not of the *user*. Users are department members; classification is
how content is sorted within and across departments.

### Skip facets; gate every page at most-restrictive cited source

Simpler implementation but defeats the broader-sharing benefit
of the new model: any page that ever cited a Confidential source
becomes Confidential-only, even though most of its content is
synthesized from Internal sources. Rejected.

### Skip facets; gate every page at least-restrictive cited source

Simplest implementation. Catastrophically wrong: leaks Confidential
content to every employee any time a wiki page cites both an Internal
and Confidential source. Rejected.

## References

- ADR 0005 — Flat departments, role-based RLS — RLS predicates
  updated to combine classification, department membership, and shares
- ADR 0006 — LLM-only wiki + directives — page facets are produced
  by the Wiki Maintainer
- ADR 0007 — Claim-level citation contract — citations are scoped
  to facet visibility
- ADR 0004 — MCP as single access layer — cross-department link
  rendering only triggers for inaccessible Confidential/Restricted
  targets
- ADR 0008 — Tiered deletion + RTBF — retention defaults vary by
  classification
- ADR 0010 — Audit ledger — classification + share grants captured
  per event
- Open question Q1 — exact retention values still need Legal sign-off
- Open question Q14 — cross-department links — re-resolved with
  facet-aware rendering
