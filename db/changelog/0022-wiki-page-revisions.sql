--liquibase formatted sql

--changeset ai-librarian:0022-wiki-page-revisions-1
--comment: Phase 2 wiki -- revision history per facet per ADR 0007. The
--comment: revision_number is per-facet (NOT per-page), so the Eng
--comment: persona facet at Confidential and the persona-neutral
--comment: facet at Internal advance independently. authored_by points
--comment: at users.id; the system sentinel is used for autonomous
--comment: Wiki Maintainer writes.
CREATE TABLE wiki_page_revisions (
	id					uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	page_id				uuid			NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
	min_classification	text			NOT NULL,
	persona_id			uuid			REFERENCES personas(id) ON DELETE RESTRICT,
	revision_number		int				NOT NULL,
	authored_by			uuid			NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
	authored_at			timestamptz		NOT NULL DEFAULT now(),
	body_markdown		text			NOT NULL DEFAULT '',

	CONSTRAINT chk_wiki_page_revisions_min_classification
		CHECK (min_classification IN ('Public','Internal','Confidential','Restricted')),

	CONSTRAINT chk_wiki_page_revisions_revno
		CHECK (revision_number >= 1)
);

COMMENT ON TABLE  wiki_page_revisions IS 'Per-facet revision history per ADR 0007. Claims + citations attach to one revision.';

CREATE INDEX ix_wiki_page_revisions_page ON wiki_page_revisions (page_id);

-- One revision_number per facet. Postgres does not allow expressions
-- inside an inline UNIQUE constraint, so the COALESCE collapse of NULL
-- persona to the sentinel UUID is expressed as a UNIQUE INDEX. Behaves
-- identically to the would-be constraint for INSERT conflict-detection
-- (23505 still surfaces).
CREATE UNIQUE INDEX uq_wiki_page_revisions_facet_revno
	ON wiki_page_revisions (
		page_id,
		min_classification,
		COALESCE(persona_id, '00000000-0000-0000-0000-000000000000'::uuid),
		revision_number
	);

ALTER TABLE wiki_page_revisions ENABLE ROW LEVEL SECURITY;
ALTER TABLE wiki_page_revisions FORCE  ROW LEVEL SECURITY;
--rollback DROP TABLE IF EXISTS wiki_page_revisions;

--changeset ai-librarian:0022-wiki-page-revisions-2
--comment: Now that wiki_page_revisions exists, wire the
--comment: page_facets.current_revision_id FK. Kept as a separate
--comment: changeset so Liquibase's dependency ordering stays clean.
ALTER TABLE page_facets
	ADD CONSTRAINT fk_page_facets_current_revision
	FOREIGN KEY (current_revision_id) REFERENCES wiki_page_revisions(id) ON DELETE SET NULL;
--rollback ALTER TABLE page_facets DROP CONSTRAINT IF EXISTS fk_page_facets_current_revision;
