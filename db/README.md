# `db/` — Postgres schema and migrations

AI Librarian's authoritative schema. Per project rules every change ships
as a Liquibase changeSet. Per [ADR 0001](../docs/adr/0001-data-platform-postgres-pgvector.md)
the database is Azure Database for PostgreSQL Flexible Server with the
`pgvector` extension.

## Layout (target)

```
db/
├── changelog/
│   ├── master.xml                          # entry point; <include> per domain
│   ├── 0000-extensions.sql                 # pgvector, citext, pg_trgm
│   ├── 0001-departments-and-users.sql      # ADR 0005
│   ├── 0002-user-authorizations.sql        # ADR 0005
│   ├── 0003-sources-and-classification.sql # ADR 0011
│   ├── 0004-source-shares.sql              # ADR 0011
│   ├── 0005-chunks-and-embeddings.sql      # pgvector + HNSW
│   ├── 0006-page-facets.sql                # ADR 0007 + persona dimension (ADR 0015)
│   ├── 0007-audit-events.sql               # ADR 0010 (partitioned monthly)
│   ├── 0008-personas.sql                   # ADR 0014
│   ├── 0009-persona-memberships.sql        # ADR 0014
│   ├── 0010-persona-action-records.sql     # ADR 0016
│   ├── 0011-persona-action-outcomes.sql    # ADR 0016
│   └── 0099-rls-policies.sql               # all RLS predicates in one place
└── seed/
    └── personas-v1.sql                     # the eight v1 personas (ADR 0014)
```

## Conventions

- **One changeSet per logical change.** Never edit a previously-applied
  changeSet; add a new one.
- **Tabs in SQL**, per `.editorconfig`.
- **Liquibase header on every file**:
	```sql
	--liquibase formatted sql

	--changeset author:001-short-name
	--comment: One-sentence description.
	-- DDL goes here.
	```
- **RLS first**. New tables get `ALTER TABLE ... ENABLE ROW LEVEL SECURITY`
  in the same changeSet that creates the table. The accompanying read /
  write predicates land in `0099-rls-policies.sql` and never get inlined.
- **Naming**: `snake_case` tables and columns; surrogate `id uuid PRIMARY KEY DEFAULT gen_random_uuid()`.
- **Audit triggers**: every business table gets a trigger that emits
  to `audit_events` per ADR 0010.

## Running migrations

Liquibase runs from a container; we don't require a local install.
A `compose` file and runbook will land alongside Phase 0's first
changeSet — until then this folder is structural only.

## Persona schema is not a visibility dimension

Reminder per [ADR 0005](../docs/adr/0005-rls-with-entra.md) and
[ADR 0014](../docs/adr/0014-personas-first-class.md): the persona tables
exist alongside the visibility tables but no RLS read predicate consults
them. Persona influences retrieval ranking, synthesis style, and
persona-action authority — never who can read what.
