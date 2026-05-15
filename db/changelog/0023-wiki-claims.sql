--liquibase formatted sql

--changeset ai-librarian:0023-wiki-claims-1
--comment: Phase 2 wiki -- immutable claims per ADR 0007. Each claim
--comment: belongs to exactly one revision; updates produce a NEW
--comment: revision with new claim rows, not in-place mutation.
--comment: position is the index within the revision (0-based).
--comment: facet_classification is denormalized from the parent
--comment: revision so the citation validator's rule-4 check can run
--comment: without joining; the data is immutable so denormalization
--comment: is safe.
CREATE TABLE wiki_claims (
	id						uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	revision_id				uuid			NOT NULL REFERENCES wiki_page_revisions(id) ON DELETE CASCADE,
	claim_text				text			NOT NULL,
	position				int				NOT NULL,
	facet_classification	text			NOT NULL,
	created_at				timestamptz		NOT NULL DEFAULT now(),

	CONSTRAINT chk_wiki_claims_position
		CHECK (position >= 0),

	CONSTRAINT chk_wiki_claims_facet_classification
		CHECK (facet_classification IN ('Public','Internal','Confidential','Restricted')),

	-- Stable ordering within a revision.
	CONSTRAINT uq_wiki_claims_revision_position UNIQUE (revision_id, position)
);

COMMENT ON TABLE  wiki_claims			IS 'Phase 2 wiki -- immutable per ADR 0007. New revisions emit new claim rows; existing rows never mutate.';
COMMENT ON COLUMN wiki_claims.facet_classification IS 'Denormalized from the parent revision facet so rule-4 (no leakage) can be checked without a join.';

CREATE INDEX ix_wiki_claims_revision ON wiki_claims (revision_id);

ALTER TABLE wiki_claims ENABLE ROW LEVEL SECURITY;
ALTER TABLE wiki_claims FORCE  ROW LEVEL SECURITY;
--rollback DROP TABLE IF EXISTS wiki_claims;
