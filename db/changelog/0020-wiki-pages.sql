--liquibase formatted sql

--changeset ai-librarian:0020-wiki-pages-1
--comment: Phase 2 wiki schema -- canonical, LLM-authored documents per
--comment: ADR 0006. One row per concept. Department-scoped; slug is
--comment: unique within a department. locked=true gates writes through
--comment: the proposed-revision approval queue (Phase 2.5 lands the
--comment: queue; the column exists now so the policy is in place when
--comment: it does).
CREATE TABLE wiki_pages (
	id				uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	department_id	uuid			NOT NULL REFERENCES departments(id) ON DELETE RESTRICT,
	slug			text			NOT NULL,
	title			text			NOT NULL,
	locked			boolean			NOT NULL DEFAULT false,
	created_at		timestamptz		NOT NULL DEFAULT now(),
	updated_at		timestamptz		NOT NULL DEFAULT now(),

	-- Slug constraints: non-empty, lowercase URL-safe characters. The
	-- application-layer slugifier produces these; the constraint is
	-- belt-and-braces.
	CONSTRAINT chk_wiki_pages_slug_format
		CHECK (slug ~ '^[a-z0-9][a-z0-9\-]{0,254}$'),

	-- Unique slug per department. Across departments the same slug is
	-- allowed (e.g. each department can have a "getting-started" page).
	CONSTRAINT uq_wiki_pages_dept_slug UNIQUE (department_id, slug)
);

COMMENT ON TABLE  wiki_pages			IS 'Phase 2 wiki -- LLM-authored canonical pages per ADR 0006. One row per concept; department-scoped.';
COMMENT ON COLUMN wiki_pages.locked		IS 'When true, writes go through the proposed-revision approval queue (ADR 0006).';

CREATE INDEX ix_wiki_pages_department ON wiki_pages (department_id);

ALTER TABLE wiki_pages ENABLE ROW LEVEL SECURITY;
ALTER TABLE wiki_pages FORCE  ROW LEVEL SECURITY;
--rollback DROP TABLE IF EXISTS wiki_pages;
