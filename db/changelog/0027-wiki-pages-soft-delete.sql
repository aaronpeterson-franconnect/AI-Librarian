--liquibase formatted sql

--changeset ai-librarian:0027-wiki-pages-soft-delete-1
--comment: Add soft-delete to wiki_pages (Tier-1 deletion per ADR 0008).
--comment: Mirrors the sources.soft_deleted_at pattern: the row stays in
--comment: the table for audit, but RLS hides it from reads (see the
--comment: 0104 policy migration). Downstream tables (page_facets,
--comment: wiki_page_revisions, wiki_claims, wiki_claim_citations) read
--comment: through EXISTS-on-wiki_pages, so hiding the page transitively
--comment: hides every revision / claim / citation on it.
ALTER TABLE wiki_pages
	ADD COLUMN soft_deleted_at timestamptz;

COMMENT ON COLUMN wiki_pages.soft_deleted_at IS 'Tier-1 deletion per ADR 0008; row hidden from RLS but kept for audit.';
--rollback ALTER TABLE wiki_pages DROP COLUMN soft_deleted_at;

--changeset ai-librarian:0027-wiki-pages-soft-delete-2
--comment: Replace the unique-per-(department, slug) constraint with a
--comment: partial unique index that only covers live rows. This lets
--comment: operators reuse a slug after soft-deleting the prior page
--comment: (the common "delete + recreate" workflow) without needing to
--comment: clean up the historical row.
ALTER TABLE wiki_pages
	DROP CONSTRAINT uq_wiki_pages_dept_slug;
CREATE UNIQUE INDEX ux_wiki_pages_dept_slug_live
	ON wiki_pages (department_id, slug)
	WHERE soft_deleted_at IS NULL;
COMMENT ON INDEX ux_wiki_pages_dept_slug_live IS 'Partial unique on (department, slug) for live pages only; soft-deleted rows do not block slug reuse.';
--rollback DROP INDEX IF EXISTS ux_wiki_pages_dept_slug_live;
--rollback ALTER TABLE wiki_pages ADD CONSTRAINT uq_wiki_pages_dept_slug UNIQUE (department_id, slug);

--changeset ai-librarian:0027-wiki-pages-soft-delete-3
--comment: Index the soft_deleted_at column so the "live pages only"
--comment: predicate that filters most queries doesn't trigger a full
--comment: scan once the corpus grows. Partial index: only the live
--comment: rows are indexed, since soft-deleted rows are rare and not
--comment: read on the hot path.
CREATE INDEX ix_wiki_pages_live
	ON wiki_pages (department_id)
	WHERE soft_deleted_at IS NULL;
--rollback DROP INDEX IF EXISTS ix_wiki_pages_live;
