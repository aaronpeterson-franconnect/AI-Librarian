# ADR 0007 — Every wiki claim must cite a real, traceable source by ID and span anchor

> Status: **Accepted (amended 2026-04-30)** · Date: 2026-04-29 · Deciders: Architect

## Amendment note

Amended on 2026-04-30 to reconcile with the page-facet model
introduced in ADR 0006 (which itself was amended in support of the
classification-as-access-boundary shift in ADR 0011). The data-model
schema and validator rules below were updated so revisions and
claims are scoped to a facet rather than a whole page, and so that
the validator enforces facet-classification scoping (a claim in an
`Internal` facet may not cite a source above `Internal`).

Amended again on 2026-04-30 in lockstep with
[ADR 0014](0014-personas-first-class.md) and
[ADR 0015](0015-persona-aware-retrieval-synthesis.md): every
retrieval and synthesis event records the **persona context** in
which it occurred. Page facets gain an optional persona dimension
(per [ADR 0006](0006-llm-only-wiki-with-directives.md) amendment);
revisions inherit the facet's persona; claim confidence may be
persona-aware. The citation contract itself is unchanged — every
claim still cites at least one source with confidence ≥ 0.7. The
amendment adds persona as a structured attribute on retrieval and
synthesis events, *not* as a visibility constraint. Persona
context also extends to autonomous-action drafts per
[ADR 0016](0016-persona-internal-autonomous-actions.md): drafts
that an autonomous action proposes for human review carry the
same claim-citation contract.

## Context

Two of the most important properties of AI Librarian — *verifiable
answers* and *right-to-be-forgotten cascade* — both depend on a single
contract: every factual statement in the wiki is linked, deterministi-
cally and machine-checkably, to the source(s) it came from.

The community implementations of the LLM Wiki pattern explored several
shapes:

- Free-text wiki pages with informal `[[source]]` mentions (most
  implementations) — easy to write, impossible to audit
- Frontmatter `sources:` lists at page level — better, but doesn't tell
  you *which claim* came from *which source*
- Per-claim citation with span anchors (the **TheKnowledge / WikiLoom**
  pattern) — high discipline but enables verification, RTBF, and
  contradiction detection

We adopt the per-claim shape and enforce it mechanically.

## Decision

### Data model

Wiki content is structured into atomic claims. A page is a topic
plus slug; each page has one or more **facets** keyed by
`min_classification` (per ADR 0006); each facet has its own revision
history; each revision contains immutable claims.

```sql
CREATE TABLE wiki_pages (
	id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	department_id   uuid NOT NULL REFERENCES departments(id),
	slug            text NOT NULL,
	title           text NOT NULL,
	locked          bool NOT NULL DEFAULT false,
	created_at      timestamptz NOT NULL DEFAULT now(),
	UNIQUE (department_id, slug)
);

CREATE TABLE page_facets (
	page_id            uuid NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
	min_classification text NOT NULL CHECK (
		min_classification IN ('Public','Internal','Confidential','Restricted')
	),
	persona_id         uuid REFERENCES personas(id),  -- nullable; NULL = persona-neutral
	current_revision_id uuid,  -- FK added after wiki_page_revisions
	updated_at         timestamptz NOT NULL DEFAULT now()
	-- Composite key shape lives in ADR 0006 amendment (NULL-safe key)
);

CREATE TABLE wiki_page_revisions (
	id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	page_id            uuid NOT NULL REFERENCES wiki_pages(id),
	min_classification text NOT NULL,  -- which classification facet this revision belongs to
	persona_id         uuid REFERENCES personas(id),  -- which persona facet (NULL = persona-neutral)
	revision_number    int  NOT NULL,
	authored_by        uuid NOT NULL,  -- 'system' user for agents
	authored_at        timestamptz NOT NULL DEFAULT now(),
	body_markdown      text NOT NULL,  -- assembled from claims for rendering
	-- (page_id, min_classification, persona_id) refs page_facets composite key
	UNIQUE (page_id, min_classification, persona_id, revision_number)
);

CREATE TABLE wiki_claims (
	id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	revision_id     uuid NOT NULL REFERENCES wiki_page_revisions(id),
	claim_text      text NOT NULL,
	position        int  NOT NULL,  -- order within the revision
	-- claims are immutable once written
	created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE wiki_claim_citations (
	claim_id        uuid NOT NULL REFERENCES wiki_claims(id),
	chunk_id        uuid NOT NULL REFERENCES chunks(id),
	span            jsonb,  -- format-specific, e.g. {"page": 7, "para": 3}
	confidence      numeric(3,2) NOT NULL CHECK (confidence BETWEEN 0 AND 1),
	PRIMARY KEY (claim_id, chunk_id)
);
```

A `wiki_claim` row is **immutable** once written. To "update" a claim
the maintainer creates a new revision with a new claim row. Each
facet of a page has its own revision sequence; facets are
regenerated together when the source pool changes (ADR 0006), but
their revision numbers are independent.

### Span anchor format

The `span` JSONB is per-format and decided by the source's Skill
plugin. Examples:

| Source type | Span shape |
|---|---|
| PDF | `{ "page": 7, "para": 3 }` |
| DOCX | `{ "section_id": "intro", "para": 2 }` |
| Code | `{ "file": "src/foo.cs", "lines": [42, 58] }` |
| SQL (Liquibase) | `{ "changeset": "2024-q4-01:add-policies-table" }` |
| Audio / video | `{ "start_seconds": 1820, "end_seconds": 1842 }` |
| Image | `{ "region": [120, 80, 540, 320] }` |
| Markdown / plain | `{ "char_start": 18043, "char_end": 18411 }` |

### Persona context on retrieval and synthesis events

Per [ADR 0014](0014-personas-first-class.md) and
[ADR 0015](0015-persona-aware-retrieval-synthesis.md), every
retrieval and synthesis event records the persona under which it
ran. This adds a `persona_id` to the audit-event details
(per [ADR 0010](0010-audit-ledger.md) amendment) for the
`query.*` family. The persona dimension does not affect citation
correctness; it enables per-persona evaluation, drift detection,
and audit attribution.

When the Wiki Maintainer produces a persona-shaped facet, the
revision's `persona_id` is populated; the persona context flows
through to the spot-check linter and any downstream evaluation
pipeline. Claim confidence may be persona-aware: the underlying
similarity score is computed identically, but the calibration of
"confidence ≥ 0.7 = strong" may be per-persona once enough
labeled data exists (v2+).

### Validator

Before any `wiki_page_revisions` row is committed, the Citation
Validator checks the proposed claims:

1. Every claim has at least one citation.
2. **Every claim has at least one citation with `confidence >= 0.7`.**
   Claims that exist only as low-confidence citations are rejected
   as weakly grounded; the maintainer must either find a stronger
   citation or omit the claim.
3. Every cited `chunk_id` exists and is not soft-deleted.
4. Every cited chunk's `source_id` exists, is approved, and belongs
   to a department the maintainer agent has authority over.
5. Every span anchor parses against the chunk's format-specific schema.
6. **Facet-classification scoping**: every cited source has a
   classification at-or-below the revision's facet
   `min_classification`. A claim in an `Internal` facet may cite
   only `Public` or `Internal` sources. A claim in a `Confidential`
   facet may cite `Public`, `Internal`, or `Confidential`. A claim
   in a `Restricted` facet may cite anything in the department.
   This is the structural guard that prevents Confidential
   information from leaking into a broadly-readable Internal facet
   (per ADR 0011).

Validation failures cause the proposed revision to be rejected. The
maintainer must produce a valid revision or skip the page.

The validator is a Postgres `BEFORE INSERT` trigger on
`wiki_page_revisions` plus an application-layer pre-check. Belt and
braces. The facet-classification check is implemented as a SQL
function comparing the revision's `min_classification` against
`max(sources.classification)` over all cited chunks' parent sources.

### Producing claims + citations — two-pass approach (per facet)

The maintainer agent produces structured output via a **two-pass
approach** rather than provider-specific JSON Schema or function-
calling features. This keeps us LLM-agnostic (ADR 0003) and avoids
locking the wiki layer to capabilities only some providers offer.

The two-pass runs **once per facet** the maintainer is producing.
For a page with both an `Internal` and a `Confidential` facet, the
maintainer runs Pass 1 + Pass 2 twice — once with the source pool
filtered to `Internal` and below, once with the full department
source pool — producing two independent revisions in the same
transaction.

Pass 1 — **synthesis**: the maintainer writes the facet as prose,
inline-citing chunks by ID (e.g., `[chunk:abc-123]`). The prompt
includes the facet's `min_classification` and the explicit
instruction "you may only cite sources at-or-below this
classification." Smaller / faster models can do this acceptably.

Pass 2 — **extraction**: a deterministic parser walks the prose,
splits it into atomic claims, resolves the inline citation tokens to
real `chunk_id` values, attaches confidence scores (computed from
embedding similarity between the claim and the cited chunk), and
emits the structured `wiki_claims` + `wiki_claim_citations` rows.

Pass 2 is *not* an LLM call — it is a parser plus a similarity
scorer. The two-pass cost is therefore dominated by Pass 1.

Trade-off accepted: this design is more robust to LLM-provider churn
and works with local models, at the cost of slightly more code in
the extraction parser and the constraint that Pass 1 must produce
inline citation tokens (enforced by the maintainer prompt + a
post-processing rejection if the prose lacks citation tokens). The
prompt-level instruction to respect facet classification is
defense-in-depth; the validator (rule 6) is the structural guard.

### Confabulation prevention

The maintainer's prompts include the rule that *every assertion in the
output must be backed by a chunk it has been shown*. The validator
enforces structurally; the prompt enforces stylistically. We accept
that a small fraction of bad-faith citations (the LLM cited a chunk
that doesn't actually support the claim) will slip through. The
**spot-check linter ships in Phase 2 with the wiki layer** (not
Phase 4 as originally drafted): a separate, cheaper model grades a
configurable percentage of newly-written claims against their cited
chunks for support quality. False-but-cited findings either trigger
maintainer regeneration or surface to Reviewers/Librarians.

## Consequences

### Easier

- RTBF cascade works: deleting a chunk identifies every dependent
  claim via FK.
- "Show me where this came from" is a one-query answer.
- Lint can detect contradictions between claims by examining their
  citations and spans.
- Audit can attribute every wiki claim to a deterministic chain:
  source → chunk → claim → revision → page.

### Harder

- The maintainer must produce structured output (claims + spans),
  not free-form prose. Heavier prompt engineering and probably
  schema-coerced outputs.
- Span extraction is per-format; each Skill must produce span-able
  chunks.
- Reading a page programmatically requires assembling claims into the
  rendered `body_markdown` (we do this on commit and store it for
  retrieval ergonomics).

### Risks

- Maintainer occasionally produces claims without proper citations
  due to model error. Mitigation: structural validator rejects and
  asks the maintainer to retry with stricter constraints.
- A "false-but-cited" claim (hallucinated content with a real chunk
  ID attached) is harder to detect. Mitigation: the Phase 2
  spot-check linter (separate model grades support quality on a
  sample of claims), plus the Reader-facing "report a bad claim"
  workflow (ADR 0006) that surfaces patterns for follow-up.

## Alternatives considered

### Page-level citation only

Easier, but breaks RTBF cascade (you'd have to regenerate the entire
page even when one citation goes away) and weakens audit.

### No citation contract; trust the LLM

Rejected. Without it none of the enterprise guarantees we promised
are deliverable.

### Provenance via watermarks / hashes only

Useful complement (we do hash chunks) but not a substitute for an
explicit per-claim relationship.

## References

- [TheKnowledge — citation validator on every write](https://github.com/badwally/TheKnowledge)
- [WikiLoom — structural provenance + claim hashing](https://github.com/do-y-lee/wikiloom)
- ADR 0006 — LLM-only wiki authoring
- ADR 0008 — Tiered deletion and RTBF
- ADR 0009 — Skill plugin pattern (defines span anchors per format)
- [ADR 0014](0014-personas-first-class.md) — Personas as a
  first-class organizing concept
- [ADR 0015](0015-persona-aware-retrieval-synthesis.md) — Persona-
  aware retrieval and synthesis (the source for the persona
  context recorded on every retrieval and synthesis event)
- [ADR 0016](0016-persona-internal-autonomous-actions.md) —
  Internal autonomous actions (action drafts inherit the citation
  contract)
