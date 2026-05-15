--liquibase formatted sql

--changeset ai-librarian:0024-wiki-claim-citations-1
--comment: Phase 2 wiki -- citation rows attached to a wiki_claim. Each
--comment: row is one (claim, chunk) tuple with a confidence + span.
--comment: span is JSONB and format-specific per the source's skill --
--comment: PDF: {"page": int}, DOCX: {"section_id": str}, etc. The
--comment: existing AiLibrarian.Domain.Citations types map onto this
--comment: row shape; the validator's mechanical rules (ADR 0007 rules
--comment: 1-5) run against these rows.
CREATE TABLE wiki_claim_citations (
	id						uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	claim_id				uuid			NOT NULL REFERENCES wiki_claims(id) ON DELETE CASCADE,
	chunk_id				uuid			NOT NULL REFERENCES source_chunks(id) ON DELETE RESTRICT,
	span					jsonb			,
	span_start				int				,
	span_end				int				,
	confidence				numeric(3,2)	NOT NULL,
	created_at				timestamptz		NOT NULL DEFAULT now(),

	CONSTRAINT chk_wiki_claim_citations_confidence
		CHECK (confidence >= 0.0 AND confidence <= 1.0),

	CONSTRAINT chk_wiki_claim_citations_span_bounds
		CHECK (span_start IS NULL OR span_end IS NULL OR (span_start >= 0 AND span_end > span_start)),

	-- A single claim citing the same chunk twice is a maintainer bug;
	-- the citation validator catches it but the unique index makes
	-- the row-level violation explicit too.
	CONSTRAINT uq_wiki_claim_citations_claim_chunk UNIQUE (claim_id, chunk_id)
);

COMMENT ON TABLE  wiki_claim_citations			IS 'Per ADR 0007 -- one row per (claim, chunk) citation. Validates against the rules in AiLibrarian.Quality.CitationValidator.';
COMMENT ON COLUMN wiki_claim_citations.span		IS 'Format-specific JSONB (PDF: page, DOCX: section_id, etc). Mirrors source_chunks.span_anchor shape.';
COMMENT ON COLUMN wiki_claim_citations.confidence IS '0.0-1.0; the validator''s rule 5 default floor is 0.7.';

CREATE INDEX ix_wiki_claim_citations_chunk ON wiki_claim_citations (chunk_id);
CREATE INDEX ix_wiki_claim_citations_claim ON wiki_claim_citations (claim_id);

ALTER TABLE wiki_claim_citations ENABLE ROW LEVEL SECURITY;
ALTER TABLE wiki_claim_citations FORCE  ROW LEVEL SECURITY;
--rollback DROP TABLE IF EXISTS wiki_claim_citations;
