--liquibase formatted sql

--changeset ai-librarian:0021-page-facets-1
--comment: Phase 2 wiki -- page facets per ADR 0006 amendment. Each
--comment: facet is one (page x classification x persona) cell. The
--comment: persona_id may be NULL for the persona-neutral facet; a
--comment: STORED generated column collapses NULL to a sentinel UUID
--comment: so the persona-neutral row coexists in the composite PK
--comment: with persona-specific rows for the same
--comment: (page, classification). (Postgres rejects expressions inside
--comment: PRIMARY KEY / UNIQUE constraint clauses -- only CREATE INDEX
--comment: takes expressions -- hence the generated column.)
--comment: current_revision_id is nullable until the first revision
--comment: lands.
CREATE TABLE page_facets (
	page_id				uuid			NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
	min_classification	text			NOT NULL,
	persona_id			uuid			REFERENCES personas(id) ON DELETE RESTRICT,
	-- Sentinel-collapsed persona key. The all-zero UUID is reserved for
	-- "persona-neutral" facets; real personas use gen_random_uuid()
	-- which never produces the zero UUID, so there is no collision.
	persona_pk			uuid			GENERATED ALWAYS AS
		(COALESCE(persona_id, '00000000-0000-0000-0000-000000000000'::uuid)) STORED,
	body_markdown		text			NOT NULL DEFAULT '',
	current_revision_id	uuid			,	-- FK added in 0022 after wiki_page_revisions exists
	created_at			timestamptz		NOT NULL DEFAULT now(),
	updated_at			timestamptz		NOT NULL DEFAULT now(),

	CONSTRAINT chk_page_facets_min_classification
		CHECK (min_classification IN ('Public','Internal','Confidential','Restricted')),

	PRIMARY KEY (page_id, min_classification, persona_pk)
);

COMMENT ON TABLE  page_facets					IS 'Per ADR 0006 amendment. One row per (page, classification, persona-or-neutral); locks live at the wiki_pages level.';
COMMENT ON COLUMN page_facets.min_classification IS 'Minimum classification a reader must clear to see this facet (ADR 0011).';
COMMENT ON COLUMN page_facets.persona_id		 IS 'NULL = persona-neutral facet; non-NULL = persona-shaped variant per ADR 0006 amendment.';

CREATE INDEX ix_page_facets_persona ON page_facets (persona_id) WHERE persona_id IS NOT NULL;

ALTER TABLE page_facets ENABLE ROW LEVEL SECURITY;
ALTER TABLE page_facets FORCE  ROW LEVEL SECURITY;
--rollback DROP TABLE IF EXISTS page_facets;
