# ADR 0006 — The wiki is LLM-authored only; librarians govern via sources, policies, directives, and page locks

> Status: **Accepted (amended 2026-04-30)** · Date: 2026-04-29 · Deciders: Architect

## Amendment note

Amended on the same day this ADR was first accepted to introduce
**page facets** in support of the cross-department collaboration
model from the amended ADR 0011. A page is no longer a single body
of text; it is a topic + slug with one or more facets, each with its
own content body and `min_classification`. The Wiki Maintainer is
now responsible for producing the appropriate facets per page based
on the classification distribution of available sources. See the
"Page facets" section below.

Amended again on 2026-04-30 in lockstep with
[ADR 0014](0014-personas-first-class.md): page facets gain an
optional **persona dimension** alongside `min_classification`. Most
pages still have only classification facets; persona facets are
produced when the source pool contains material that substantively
re-shapes the right answer for a given persona's work-context. A
page about "Customer onboarding" might have an `Internal × no-persona`
facet (general view) and an `Internal × Engineering` facet (the
same general view re-shaped for engineering work). Persona is *not*
a visibility dimension (per [ADR 0005](0005-rls-with-entra.md)
amendment); persona facets are gated by their `min_classification`
exactly like any other facet. See the "Persona dimension on page
facets" subsection below.

## Context

The Karpathy LLM Wiki pattern centers on an LLM-authored, compounding
wiki — humans curate sources, the LLM writes and maintains all
synthesis. The community implementations explore variants:

- **Pure LLM-authored** (the original): clean separation; humans only
  edit sources
- **Hybrid** (WikiLoom): LLM drafts, humans can edit; AI-edit markers
  prevent the LLM from overwriting human content
- **Free-for-all**: humans edit anything, LLM helps

Each has trade-offs:

- Pure LLM-authored produces the cleanest data model and the most
  predictable behavior, but offers librarians no direct lever when the
  LLM produces a subtly-wrong page
- Hybrid is human-friendly but couples human edits to AI re-generation
  in ways that can drift over time and make right-to-be-forgotten hard
- Free-for-all sacrifices the discipline that makes the wiki
  trustworthy

Our scope mandates RTBF with cascade regeneration (ADR 0008) and
verifiable citations on every claim (ADR 0007). Both depend on
deterministic claim-to-source linkage. Hybrid authoring threatens that
contract: free-text human edits are not structurally bound to sources.

## Decision

The wiki is **LLM-authored only**. No human ever directly edits a
`wiki_pages` row, a `wiki_page_revisions` row, a `wiki_claims` row, or
a `wiki_claim_citations` row. The Wiki Maintainer agent and the
Cascade-Regeneration Worker are the only writers.

Librarians govern wiki content through **four indirect levers**:

### 1. Sources

What goes into the corpus shapes what goes into the wiki. Librarians
approve, reject, and remove sources via the approval queue and the
delete flows.

### 2. Department policy

Each department has a versioned `department_policies` row containing:

```yaml
in_scope:
	- list of topic descriptions
out_of_scope:
	- list of topic descriptions
quality_threshold: 0.7
classification_default: Internal
approval_rules:
	auto_approve_for_roles: [Librarian]
	require_approval_for:
		- external_sources
		- classification: Restricted
retention:
	default_days: 2555
```

The ingestion editorial filter consults the policy before accepting a
source. The Wiki Maintainer consults it before integrating sources
into the wiki.

### 3. Directives

A `department_directives` table holds persistent **content-level
guidance** that the Wiki Maintainer must obey on every page write:

```yaml
- id: directive-001
	scope: department  # or page glob
	text: "Always describe the credit-line policy as 'risk-tiered' rather than 'tiered'."
	priority: high
	created_by: librarian@finance
	created_at: 2026-04-29
	expires_at: null  # optional; no default TTL
- id: directive-002
	scope: page:/wiki/engineering/secrets/*
	text: "Reference the 2026 Q1 rotation runbook when secrets rotation is mentioned."
	priority: medium
```

Directives are versioned, audited, and surfaced to librarians in the
portal. They can target the whole department, a path glob of pages, a
specific page, or a tag set.

**TTL is optional with no default.** Directives persist until a
librarian removes them. We accept that this puts the burden of
hygiene on the librarian; we mitigate by surfacing directive
inventory and "directive last consulted" telemetry in the librarian
dashboard so stale directives are visible.

The Wiki Maintainer's prompt loads relevant directives at the
beginning of every regeneration. Conflicts are resolved by
**priority > scope-specificity > recency** (Q12 closed):

1. A `priority: high` directive always wins over `medium` or `low`.
2. Among equal priorities, narrower scope wins (page-glob beats
   department-wide).
3. Among equal priority and scope, newest wins.
4. Contradictory directives at same priority and same scope →
   maintainer logs a `directive.conflict` audit event, proceeds
   with the newer, surfaces the conflict in the librarian dashboard.

Priority tiers are `low`, `medium` (default), and `high`. `high` is
reserved for librarian-issued urgent corrections.

### 4. Page locks

A `locked: bool` flag on `wiki_pages`. When `true`, the Wiki Maintainer
cannot commit to the page. Instead, it produces a **proposed revision**
that lands in the approval queue. A Reviewer or Librarian reviews the
diff and either approves (the proposed revision becomes the new
current revision) or rejects (the proposed revision is discarded with
a reason).

**Either a Reviewer or a Librarian can lock or unlock a page.** A
Reviewer who notices a page going off the rails shouldn't have to
escalate to lock it. Both lock and unlock events are audited.

Page locks are intended to be the exception, not the rule: high-stakes
pages where a regeneration-gone-wrong is unacceptable. Most pages
remain unlocked.

**Proposed-revision SLAs (Q13 closed)**:

- Default expiry: **14 calendar days** if no Reviewer/Librarian
  decision. On expiry: auto-rejected with reason
  `"expired without review"`; the next regeneration trigger may
  produce a fresh proposal.
- Soft escalation at 5 business days → notify Librarian role group
  + surface "queue health" card on the librarian dashboard.
- Hard escalation at 10 business days → notify Admin.
- Per-department override allowed in `policy.yaml`.

Approval-queue SLAs for pending sources (Q17 closed) share these
same constants by design — librarians have one mental model.

### Page facets — content variants per visibility tier

A wiki page is no longer a single body of text. A page is a topic
identified by a slug (e.g., `/wiki/customer-onboarding`) with **one
or more facets**:

```sql
CREATE TABLE wiki_pages (
	id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	department_id uuid NOT NULL REFERENCES departments(id),
	slug          text NOT NULL,
	title         text NOT NULL,
	locked        bool NOT NULL DEFAULT false,
	UNIQUE (department_id, slug)
);

CREATE TABLE page_facets (
	page_id            uuid NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
	min_classification text NOT NULL CHECK (
		min_classification IN ('Public','Internal','Confidential','Restricted')
	),
	body_markdown      text NOT NULL,
	current_revision   uuid NOT NULL,
	updated_at         timestamptz NOT NULL DEFAULT now(),
	PRIMARY KEY (page_id, min_classification)
);
```

Each facet has its own revision history; locks apply at the page
level (locking a page locks all its facets).

#### How the Wiki Maintainer produces facets

For a page about a topic, the Maintainer evaluates the available
source pool (sources visible at each classification tier in the
owning department) and produces facets according to this rule:

```
Let SOURCES_INTERNAL    = sources of classification ≤ Internal
Let SOURCES_CONFIDENTIAL = sources of classification ≤ Confidential
Let SOURCES_RESTRICTED  = sources of classification ≤ Restricted

If SOURCES_INTERNAL is non-empty:
    produce Internal facet from SOURCES_INTERNAL only
If SOURCES_CONFIDENTIAL has new content beyond SOURCES_INTERNAL:
    produce Confidential facet from SOURCES_CONFIDENTIAL
If SOURCES_RESTRICTED has new content beyond SOURCES_CONFIDENTIAL:
    produce Restricted facet from SOURCES_RESTRICTED
```

A facet is only produced if the source pool at that tier
contains material the lower facet wouldn't have. This avoids
generating identical Internal and Confidential facets when no
Confidential sources contribute new content.

Citations within a facet only reference sources visible at that
facet's classification level. A `Confidential` source never
appears as a citation in the `Internal` facet — that would leak
the Confidential source's title.

When facets exist alongside the Internal facet, the Internal
facet body includes a small footer:

> *Additional content from {Department}-restricted sources is
> available to {Department} members.*

This tells readers that more exists without revealing what.

#### How readers see facets

`read_page(slug)` returns the **highest-classification facet the
caller can access**. This is the most-detailed view available to
that user:

| Caller | Returned facet |
|---|---|
| External / unauthenticated | Public facet only (rare) |
| Employee outside owning department | Internal facet |
| Member of owning department | Confidential facet (which includes Internal content + Confidential additions) |
| Librarian / Admin in owning department | Restricted facet (the most detailed) |

A reader sees one rendered page; the facet chosen is invisible
in the URL. The portal renders a classification badge so the
reader knows what tier they're seeing.

#### Why this matters

This is the structural mechanism that makes the cross-department
collaboration model from ADR 0011 trustworthy. A wiki page about
"How we onboard customers" can have:

- An **Internal facet** describing the general process and team
  responsibilities — readable across the company
- A **Confidential facet** that includes specific dollar
  thresholds, sensitive customer SLAs, and competitive positioning
  — readable only inside the Sales department

Without facets, we'd either gate the whole page at Confidential
(losing the broadly useful general-process information) or leak
the Confidential content company-wide. Facets are the answer.

See ADR 0011 for the classification model and ADR 0007 for the
citation implications.

### Persona dimension on page facets

Per [ADR 0014](0014-personas-first-class.md), persona is a fourth
organizing dimension. Some wiki pages benefit from a persona-shaped
facet alongside the classification-shaped facets defined above.

The schema extends `page_facets` with an optional `persona_id`
column. The composite key becomes
`(page_id, min_classification, persona_id)`, where `persona_id`
may be `NULL` for the persona-neutral facet:

```sql
ALTER TABLE page_facets
	ADD COLUMN persona_id uuid REFERENCES personas(id);

ALTER TABLE page_facets
	DROP CONSTRAINT page_facets_pkey,
	ADD PRIMARY KEY (page_id, min_classification, COALESCE(persona_id, '00000000-0000-0000-0000-000000000000'));
```

(The exact migration form is finalized in the Phase 0 changelog
revision; the conceptual key is shown above.)

Within a `(page_id, min_classification)` pair, there is always a
persona-neutral facet (`persona_id IS NULL`), and optionally one
or more persona-shaped facets. The persona-neutral facet is the
default content; persona facets exist only when adding them is
substantive.

#### When to produce a persona facet

The Wiki Maintainer produces a persona facet for a page only when
**both** are true:

1. The page is in scope for the persona (its topic intersects the
   persona's work-context per the persona brief)
2. The source pool at the relevant classification tier contains
   material that *substantively re-shapes* the right answer for
   that persona — distinct emphasis, different examples, code-
   versus-narrative orientation, etc.

The Maintainer's prompt makes the determination; a librarian can
override by adding a directive that either forces or forbids a
persona facet for a page glob.

#### Persona facets are gated by classification, not by persona

Persona is *not* a visibility dimension
(per [ADR 0005](0005-rls-with-entra.md)). The RLS read policy on
`page_facets` continues to gate solely on `min_classification`:

- An `Internal × Engineering` facet is readable by any
  authenticated employee, just like an `Internal × no-persona`
  facet
- A `Confidential × Engineering` facet is readable by Engineering
  department members, just like a `Confidential × no-persona` facet

The reader's *primary persona* (per
[ADR 0015](0015-persona-aware-retrieval-synthesis.md)) determines
*which facet they see by default* — a Sales-persona reader sees
the `Sales` facet (if one exists at their classification tier);
otherwise they see the persona-neutral facet at their tier. A
reader can request any persona facet visible to them at their
classification tier.

#### Citation scoping is unchanged

Citations within a persona facet still respect the facet's
`min_classification` (per
[ADR 0007](0007-claim-level-citation-contract.md) rule 6). Persona
does not affect what may be cited — only how the claims are
shaped.

#### Locks apply at the page level, unchanged

Locking a page locks **all** of its facets — every classification
× persona combination. This keeps the lock semantics clean: a
locked page has a single approval queue across all facets.

### Bonus lever — Reader feedback ("report a bad claim")

To close the loop from Readers back to Reviewers and Librarians,
every claim rendered in the portal includes a "report" affordance.
A Reader who sees a claim that looks wrong submits a free-text
reason, which creates a `wiki_claim_reports` row visible in the
Reviewer/Librarian dashboard.

A Reviewer or Librarian triages each report:

- **Valid** — triggers maintainer regeneration of the page with the
  report context included as additional guidance
- **Promote to directive** — the underlying concern becomes a
  persistent directive (with the librarian's wording)
- **Invalid** — closes the report with a reason

This feedback loop is the early-warning system for LLM quality
issues. Without it, librarians have to find problems by reading the
wiki cold. Phase 2 deliverable.

## Consequences

### Easier

- Wiki schema is clean: every row is machine-written and citation-
  bound. RTBF cascades work deterministically.
- Citation-validator contract (ADR 0007) is enforceable because there
  is no free-text human content path.
- Audit trail is straightforward: every write has a single agent
  identity and a single triggering event.
- Librarian UX is bounded: their interactions are policy editing,
  directive management, source approval, page-lock decisions. Each is
  a distinct, well-scoped tool.

### Harder

- Librarians cannot fix a wrong wiki page directly. They must
  diagnose ("which source caused this?", "is a directive missing?",
  "should this page be locked while we sort it out?") and steer the
  maintainer indirectly.
- Some wrongness will be resistant to indirect levers. We need to
  monitor for "directive drift" — librarians piling up directives to
  paper over LLM problems we should be solving at the maintainer
  layer.

### Risks

- Librarians get frustrated and start asking for direct edit access.
  Mitigation: solid directive UX, fast feedback loops, instrumentation
  so we can see what kinds of corrections librarians are issuing.
- A bug in the maintainer produces persistent low-quality pages.
  Mitigation: linter coverage, the Reader "report a bad claim"
  workflow (Phase 2) that surfaces patterns to Reviewers and
  Librarians.
- Directive sets grow unbounded. Mitigation: portal-surfaced
  directive inventory + "last consulted" telemetry; no TTL by
  default but librarians can set one per directive.

## Alternatives considered

### Hybrid (LLM-drafted, human-finalized with AI-edit markers)

The WikiLoom pattern. Librarian-friendly, but couples free-text human
content to the citation contract in ways we don't see how to enforce
mechanically. Rejected for v1; reconsider in Phase 5 if directive
drift becomes a real problem.

### Free-for-all

Sacrifices the structural guarantees we need for RTBF and
verifiable citations. Rejected.

### LLM-only with no librarian levers (pure Karpathy)

Cleanest, but leaves librarians powerless when things go wrong.
The directive + lock additions cost little and add a real escape
hatch.

## References

- [Karpathy LLM Wiki gist](https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f)
- [WikiLoom — hybrid authoring with AI markers](https://github.com/do-y-lee/wikiloom)
- ADR 0007 — Claim-level citation contract (citations are scoped
  to facet visibility)
- ADR 0008 — Tiered deletion and RTBF
- ADR 0011 — Data classification (the source for facet visibility tiers)
- ADR 0005 — RLS predicates (the read gate on page facets)
- [ADR 0014](0014-personas-first-class.md) — Personas as a
  first-class organizing concept (the source for the persona
  dimension on facets)
- [ADR 0015](0015-persona-aware-retrieval-synthesis.md) — Persona-
  aware retrieval (drives which persona facet a reader sees by
  default)
- Open question Q12 — Directive precedence rules
- Open question Q13 — Page-lock SLAs
