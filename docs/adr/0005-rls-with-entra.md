# ADR 0005 — Flat departments, role-based access via Entra groups, enforced with Postgres RLS

> Status: **Accepted (amended 2026-04-30)** · Date: 2026-04-29 · Deciders: Architect

## Amendment note

Originally accepted on 2026-04-29 with read access gated strictly by
department membership. Later the same day, ADR 0011 was amended to
make classification the default access boundary, supporting the
common case of cross-department reading of `Internal` content. This
ADR was updated in lockstep: the read-side RLS predicates now combine
classification, department membership, and source-share grants. The
write-side predicates are unchanged.

Amended again on 2026-04-30 in lockstep with
[ADR 0014](0014-personas-first-class.md), which introduces persona
as a fourth organizing dimension. **Persona is explicitly not a
visibility dimension.** Visibility (read access) stays governed by
classification + department + source shares as defined in this ADR.
Persona affects retrieval ranking, synthesis style, and the
authority to invoke autonomous internal actions; it never widens
*or narrows* the authorized result set. The "Persona is not part
of the visibility predicate" section below is added to make the
guard explicit at the policy layer.

## Context

The system serves multiple departments (Engineering, Finance, Legal,
HR, etc.). Each department has its own corpus and its own librarian(s).

Most knowledge in those corpora is `Internal` and is meant to be
readable across the company — engineering coding standards, marketing
positioning, operational runbooks, vendor management policies. A
known minority is `Confidential` or `Restricted` and must stay
department-scoped.

Critical requirements:

- Reads of `Internal` (and `Public`) content are open to all
  authenticated employees
- Reads of `Confidential` content are gated to the owning
  department's members and any explicitly-shared departments
- Reads of `Restricted` content are gated to the owning
  department's `Librarian`+ members and any explicitly-shared
  departments
- **Write access** (submit / approve / govern / delete) is gated
  strictly by department + role membership, regardless of
  classification — classification never broadens write authority
- Authorization must be enforced at the database layer, not just in
  application code (defense in depth, and to make a leak structurally
  impossible)
- Identity must be Microsoft Entra ID (the organization's single sign-
  on)
- Access management must be operationally simple — IT should be able
  to grant a user access by adding them to a group, with no system-
  specific tooling

We considered modeling departments as a tree with role inheritance
down the tree. We rejected that complexity for v1. Departments are
flat. If a person needs librarian rights on multiple departments, IT
adds them to multiple groups.

## Decision

### Department model — flat

```sql
CREATE TABLE departments (
	id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	name          text NOT NULL UNIQUE,
	display_name  text NOT NULL,
	policy_id     uuid REFERENCES department_policies(id),
	created_at    timestamptz NOT NULL DEFAULT now(),
	deactivated_at timestamptz
);
```

There is no `parent_id`, no path column, no implicit access
inheritance. If "Backend" is a meaningful boundary inside Engineering,
it gets its own row in `departments`.

### Role model

Five roles. Within a department, the first four form a strict
ladder — each role includes everything the prior role can do, plus
new capabilities. `Admin` is system-wide and orthogonal.

| Role | Read | Submit sources | Approve queue + soft-delete | Edit policy / directives | Lock pages, hard-delete, initiate concept RTBF | System-wide |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| `Reader` | ✓ | | | | | |
| `Contributor` | ✓ | ✓ | | | | |
| `Reviewer` | ✓ | ✓ | ✓ | | | |
| `Librarian` | ✓ | ✓ | ✓ | ✓ | ✓ | |
| `Admin` | system-wide | | | | system-wide | ✓ |

**Reviewer** separates "I decide what enters the wiki" from "I set
the editorial direction." A senior engineer can be a Reviewer for
the team without holding policy authority.

**Admin** is system-wide only: create departments, assign librarians
in any department, view all audit, run system-wide concept RTBF.
Department-scoped administrative work happens via `Librarian`.

A user with `Librarian` on `Engineering` is **not** automatically a
librarian on a separate `Backend` department. They are independent
grants. Departments are flat; access does not inherit.

### Identity model — Entra groups map directly to (department, role)

Each (department, role) tuple maps to exactly one Entra ID group.
**The exact naming convention follows IT's existing standard for
app-scoped Entra groups** (see resolved Q5). The system reads the
mapping from configuration and does not assume a specific format.

Conceptually, expect names of the form
`{IT-prefix}-{Department}-{Role}` — for example, an organization
that already prefixes app groups `APP-` would use
`APP-AILIB-Engineering-Librarian`. AI Librarian's group-sync logic
treats the names as opaque strings and matches by configured
mapping, so we never bake a literal naming pattern into code.

A user's effective access is the union of all groups they belong to.
A person who is "Engineering Librarian and Finance Reader" belongs to
two groups; that's two rows in `user_authorizations`.

A nightly + on-demand sync job reads Microsoft Graph and materializes
the authorizations:

```sql
CREATE TABLE user_authorizations (
	user_id           uuid NOT NULL,
	department_id     uuid NOT NULL REFERENCES departments(id),
	role              text NOT NULL CHECK (
		role IN ('Reader','Contributor','Reviewer','Librarian','Admin')
	),
	source_group_id   text NOT NULL,  -- Entra group object ID
	granted_at        timestamptz NOT NULL DEFAULT now(),
	PRIMARY KEY (user_id, department_id, role)
);

CREATE INDEX user_authorizations_user_idx ON user_authorizations (user_id);
```

The `Admin` role is special: it is granted with `department_id = NULL`
and applies system-wide.

### Authorization at request time

Every API entry point — REST endpoints, MCP tools, background jobs
acting on behalf of a user — sets these Postgres session variables on
the connection at the start of each request:

```sql
SET LOCAL app.user_id            = '<user oid>';
SET LOCAL app.is_authenticated   = 'true' | 'false';
SET LOCAL app.is_employee        = 'true' | 'false';
SET LOCAL app.department_ids     = '<UUIDs of user's home departments>';
SET LOCAL app.contributor_depts  = '<UUIDs where user is Contributor or higher>';
SET LOCAL app.reviewer_depts     = '<UUIDs where user is Reviewer or higher>';
SET LOCAL app.librarian_depts    = '<UUIDs where user is Librarian or higher>';
SET LOCAL app.is_admin           = 'true' | 'false';
```

`app.is_employee` is **true** when the user is a tenant member
(not a B2B guest). The `Internal` company-wide read access
documented below requires `is_employee = true` — B2B guests are
authenticated but **not** treated as company-wide readers; their
access falls back to strict department membership and explicit
`source_shares` grants. This preserves Pattern B from the IT brief
(B2B guest scoped to a single department) under the
classification-driven model.

`app.department_ids` is the user's *home* departments — the ones
they hold any role in. It is **no longer** the full set of
departments they can read; classification + share grants extend
read access beyond home departments for employees.

Each role-specific list is a strict superset of the next: librarian
⊆ reviewer ⊆ contributor ⊆ home departments. These are still used
for write predicates and they don't change.

These are computed by the application from `user_authorizations`
once at sign-in and cached on the request context. The Postgres
connection pool resets session state between requests
(`Reset` semantics in Npgsql).

### RLS policies — reads (classification-aware)

Every source-bearing table has RLS enabled. The read predicate
combines classification, department membership, and shares:

```sql
ALTER TABLE sources ENABLE ROW LEVEL SECURITY;

CREATE POLICY sources_read ON sources
	FOR SELECT
	USING (
		-- Admin sees all
		current_setting('app.is_admin')::bool

		-- Public sources are world-readable
		OR classification = 'Public'

		-- Internal sources are visible to any authenticated employee.
		-- B2B guests are excluded here; they read only via
		-- department membership or explicit shares.
		OR (classification = 'Internal'
		    AND current_setting('app.is_employee')::bool)

		-- Confidential sources are visible to home department members
		OR (classification = 'Confidential'
		    AND department_id::text = ANY (
		        string_to_array(current_setting('app.department_ids'), ',')
		    ))

		-- Restricted sources are visible only to Librarian+ in the
		-- owning department
		OR (classification = 'Restricted'
		    AND department_id::text = ANY (
		        string_to_array(current_setting('app.librarian_depts'), ',')
		    ))

		-- Explicit shares: any classification can be shared with a
		-- specific department; the share's expiry is checked
		OR EXISTS (
			SELECT 1 FROM source_shares ss
			WHERE ss.source_id = sources.id
			  AND ss.shared_with_department_id::text = ANY (
			      string_to_array(current_setting('app.department_ids'), ',')
			  )
			  AND (ss.expires_at IS NULL OR ss.expires_at > now())
		)
	);
```

A short helper makes the predicate readable at call sites:

```sql
CREATE FUNCTION app.can_read_source(s sources) RETURNS bool
LANGUAGE sql STABLE AS $$
	SELECT
		current_setting('app.is_admin')::bool
		OR s.classification = 'Public'
		OR (s.classification = 'Internal'
		    AND current_setting('app.is_employee')::bool)
		OR (s.classification = 'Confidential'
		    AND s.department_id::text = ANY (
		        string_to_array(current_setting('app.department_ids'), ',')
		    ))
		OR (s.classification = 'Restricted'
		    AND s.department_id::text = ANY (
		        string_to_array(current_setting('app.librarian_depts'), ',')
		    ))
		OR EXISTS (
			SELECT 1 FROM source_shares ss
			WHERE ss.source_id = s.id
			  AND ss.shared_with_department_id::text = ANY (
			      string_to_array(current_setting('app.department_ids'), ',')
			  )
			  AND (ss.expires_at IS NULL OR ss.expires_at > now())
		)
$$;
```

Tables that contain extracted source content (`source_chunks`,
`source_embeddings`, `wiki_citations`) gain an equivalent policy
that joins to `sources` and applies `app.can_read_source`.

### RLS policies — wiki page facets

The wiki uses page facets (see ADR 0011). Each row in `page_facets`
carries a `min_classification` indicating who may read it. Read
gating mirrors the source rules:

```sql
ALTER TABLE page_facets ENABLE ROW LEVEL SECURITY;

CREATE POLICY page_facets_read ON page_facets
	FOR SELECT
	USING (
		current_setting('app.is_admin')::bool
		OR (min_classification = 'Public')
		OR (min_classification = 'Internal'
		    AND current_setting('app.is_authenticated')::bool)
		OR (min_classification = 'Confidential'
		    AND department_id::text = ANY (
		        string_to_array(current_setting('app.department_ids'), ',')
		    ))
		OR (min_classification = 'Restricted'
		    AND department_id::text = ANY (
		        string_to_array(current_setting('app.librarian_depts'), ',')
		    ))
	);
```

Note: page facets cannot be share-targeted directly — share grants
are on individual sources, not on pages. If a department needs a
shared department's full wiki view, that's a different operational
pattern (cross-listing the department in their group memberships).

### RLS policies — writes (unchanged)

Write and approval policies key off the role-specific lists, exactly
as before. Classification never broadens write authority.

Inserting a source requires Contributor; approving a queued source
or soft-deleting requires Reviewer; hard-deleting or editing policy
requires Librarian:

```sql
CREATE POLICY sources_insert ON sources
	FOR INSERT
	WITH CHECK (
		current_setting('app.is_admin')::bool
		OR department_id::text = ANY (
			string_to_array(current_setting('app.contributor_depts'), ',')
		)
	);

CREATE POLICY sources_soft_delete ON sources
	FOR UPDATE
	USING (
		current_setting('app.is_admin')::bool
		OR department_id::text = ANY (
			string_to_array(current_setting('app.reviewer_depts'), ',')
		)
	);

CREATE POLICY sources_hard_delete ON sources
	FOR DELETE
	USING (
		current_setting('app.is_admin')::bool
		OR department_id::text = ANY (
			string_to_array(current_setting('app.librarian_depts'), ',')
		)
	);
```

Helper SQL functions wrap these checks for readability:

```sql
-- Read helper takes the source row to evaluate classification + share
CREATE FUNCTION app.can_read_source(s sources) RETURNS bool ...

-- Write helpers take a department id (classification doesn't apply)
CREATE FUNCTION app.can_contribute(dept_id uuid) RETURNS bool ...
CREATE FUNCTION app.can_review(dept_id uuid) RETURNS bool ...
CREATE FUNCTION app.can_govern(dept_id uuid) RETURNS bool ...   -- Librarian or Admin
```

### Source shares — the explicit cross-department exception

`source_shares` (defined in ADR 0011) is the only mechanism that
broadens read access for a `Confidential` or `Restricted` source
beyond the owning department. The table is consulted in the read
predicate above; insert/update/delete on it has its own policy:

```sql
ALTER TABLE source_shares ENABLE ROW LEVEL SECURITY;

-- Only Librarian of the owning department or Admin may grant a share
CREATE POLICY source_shares_grant ON source_shares
	FOR INSERT
	WITH CHECK (
		current_setting('app.is_admin')::bool
		OR EXISTS (
			SELECT 1 FROM sources s
			WHERE s.id = source_shares.source_id
			  AND s.department_id::text = ANY (
			      string_to_array(current_setting('app.librarian_depts'), ',')
			  )
		)
	);

-- The granting Librarian or Admin may revoke
CREATE POLICY source_shares_revoke ON source_shares
	FOR DELETE
	USING (
		current_setting('app.is_admin')::bool
		OR EXISTS (
			SELECT 1 FROM sources s
			WHERE s.id = source_shares.source_id
			  AND s.department_id::text = ANY (
			      string_to_array(current_setting('app.librarian_depts'), ',')
			  )
		)
	);
```

`Restricted` sources require Admin to grant a share — Librarian
alone is insufficient. This is enforced in the API layer (the
RLS predicate above is permissive at the Librarian level; the API
adds the Admin-only requirement for `Restricted`). A future
Postgres CHECK constraint could enforce this at the DB layer.

### Persona is not part of the visibility predicate

[ADR 0014](0014-personas-first-class.md) introduces persona as a
fourth organizing dimension. To be unambiguous: **persona does not
appear in any RLS read predicate, anywhere in the system.**

- The session variables listed above (`app.user_id`,
  `app.is_employee`, `app.department_ids`, role lists,
  `app.is_admin`) are the *only* inputs to the read predicate
- A `app.persona_id` session variable is set per session for
  retrieval ranking, synthesis style, and persona-action
  authorization (per [ADR 0015](0015-persona-aware-retrieval-synthesis.md)
  and [ADR 0016](0016-persona-internal-autonomous-actions.md));
  RLS policies do not consult it
- The page-facet RLS policy gates on `min_classification` only;
  the persona dimension on facets (per
  [ADR 0006](0006-llm-only-wiki-with-directives.md) amendment) is
  a *content-shape* dimension, not a visibility dimension. A
  persona-shaped facet is gated by its `min_classification` like
  any other facet.

The structural reason this matters: persona-aware retrieval runs
*after* RLS has already filtered the candidate set. Persona ranks
and shapes; classification + department + shares decide what's
in the set in the first place. This ordering is documented in
[ADR 0015](0015-persona-aware-retrieval-synthesis.md).

A reviewer evaluating any future change that would consult persona
in an RLS read predicate must reject the change and route it
through this ADR.

### Persona-membership write authority

Persona memberships (`persona_memberships` per
[ADR 0014](0014-personas-first-class.md)) are managed by:

- **Admin** — system-wide
- **Librarian within their department's scope** — may grant or
  revoke a persona membership where `persona_memberships.department_id`
  matches a department in `app.librarian_depts`

This extends the role table to: a Librarian can manage persona
memberships within their own department but not for other
departments; an Admin can manage memberships system-wide.

### Background-agent identity

Background workers (Wiki Maintainer, Linter, Cascade-Regeneration
Worker) run under a managed identity that maps to a system user with
`Admin` role. Every action they take records the *human* user who
triggered the chain (e.g., "user X uploaded source Y, the Wiki
Maintainer regenerated page Z") via the `originated_by` audit field
so attributions remain accurate.

## Consequences

### Easier

- Granting access = adding a user to an Entra group. IT already does
  this in their sleep.
- Adding a department is one row in `departments` plus five Entra
  groups created by IT.
- Cross-department reading is the default for `Internal` content —
  no user request, no librarian approval, just works.
- One-off Confidential sharing has a first-class mechanism
  (`source_shares`) that's auditable and revocable.

### Harder

- A person with broad responsibilities (Engineering Librarian + Data
  Librarian + Security Librarian) belongs to several groups instead
  of one. We accept this; it's how IT already manages access.
- We forgo automatic inheritance of access for sub-organizations.
  If the org grows departmental sub-structure later, we re-evaluate.
- A "view across all my departments" UI must aggregate explicitly
  rather than walking a tree.
- The read RLS predicate is more complex than a single equality
  check (classification + department + shares). Mitigation: the
  predicate is wrapped in `app.can_read_source` for readability,
  and the test suite exhaustively covers each branch. Performance
  impact is small — `app.is_admin` short-circuits, the
  classification check is a single text equality, the department
  check is a string-array intersection, and the share lookup uses
  a covering index on `source_shares (source_id, shared_with_department_id)`.

### Risks

- Group creation gets out of hand if departments proliferate.
  Mitigation: a documented IT runbook for department lifecycle, and
  a reconciliation job that reports orphan groups (group exists in
  Entra without a matching `departments` row, or vice versa).
- An admin clicks the wrong button in Entra and accidentally grants
  Engineering access to Finance. Mitigation: production-grade naming
  convention (Q5) and the audit ledger captures the permission change
  through the nightly sync, so we can trace and revert.
- The read predicate's classification branch creates a new failure
  mode: misclassifying a sensitive source as `Internal` exposes it
  company-wide. Mitigation: PII-detection Skill flags suspect
  content pre-approval; per-department default classification can
  be set to `Confidential` in `policy.yaml`; periodic sentinel job
  flags `Internal` sources whose content matches sensitive patterns.
- Stale share grants accumulate. Mitigation: `source_shares.expires_at`
  is encouraged in the API; a librarian dashboard surfaces grants
  with no expiry that haven't been touched in 90 days.

## Alternatives considered

### Hierarchical departments with `ltree` paths and inherited access

The original proposal. More expressive, but adds path-expansion
logic, more complex RLS predicates, and a non-obvious mental model
for IT. Rejected for v1 in favor of operational simplicity. Document-
ed here so the trade-off is preserved if we revisit.

### Application-only access checks

Rejected. Every team eventually has a query that bypasses the
service layer. RLS is the only way to make leakage structurally
impossible.

### Per-department database

Strong isolation but breaks cross-department features (system-wide
linter, admin search) and multiplies operational cost.

### A separate authorization service (Cerbos / OpenFGA / SpiceDB)

Powerful but introduces another runtime dependency for marginal
benefit at our scale. Postgres RLS is sufficient and has better
locality.

## References

- ADR 0001 — Postgres + pgvector
- ADR 0011 — Data classification as default access boundary
  (the read-side predicates above are derived from this ADR)
- ADR 0008 — Tiered deletion and RTBF (depends on cascade-safe RLS)
- [ADR 0014](0014-personas-first-class.md) — Personas as a
  first-class organizing concept (the dimension RLS deliberately
  does *not* consult)
- [ADR 0015](0015-persona-aware-retrieval-synthesis.md) — Persona-
  aware retrieval (runs *after* RLS filtering)
- [ADR 0016](0016-persona-internal-autonomous-actions.md) — How
  persona scopes autonomous internal actions
- [Open Brain Row Level Security primitive](https://github.com/NateBJones-Projects/OB1/tree/main/primitives/rls)
- Open question Q5 — Entra group naming convention
