--liquibase formatted sql

--changeset ai-librarian:0025-wiki-claim-grades-1
--comment: Phase 2 wiki -- LLM-as-judge grade persistence. Replaces the
--comment: AiLibrarian.Quality.InMemoryClaimGradeSink from the Phase 1
--comment: hardening Open Item #1 (deferred to here). One grade per
--comment: (claim, grader_version); historical grader runs are kept so
--comment: a model upgrade can be compared against the prior run.
--comment: grader_version is operator-supplied (e.g. "gpt-4o-2026-01")
--comment: and uniquely identifies one grading pass.
CREATE TABLE wiki_claim_grades (
	id					uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	claim_id			uuid			NOT NULL REFERENCES wiki_claims(id) ON DELETE CASCADE,
	verdict				text			NOT NULL,
	confidence			numeric(3,2)	NOT NULL,
	rationale			text			NOT NULL DEFAULT '',
	grader_version		text			NOT NULL,
	graded_at			timestamptz		NOT NULL DEFAULT now(),

	CONSTRAINT chk_wiki_claim_grades_verdict
		CHECK (verdict IN ('Supported','NotSupported','Partial','Unverifiable')),

	CONSTRAINT chk_wiki_claim_grades_confidence
		CHECK (confidence >= 0.0 AND confidence <= 1.0),

	CONSTRAINT uq_wiki_claim_grades_claim_version UNIQUE (claim_id, grader_version)
);

COMMENT ON TABLE  wiki_claim_grades					IS 'LLM-as-judge grades per ADR 0007. One row per grader run; historical runs preserved for model-upgrade comparison.';
COMMENT ON COLUMN wiki_claim_grades.verdict			IS 'Supported / NotSupported / Partial / Unverifiable -- maps to AiLibrarian.Domain.Citations.ClaimVerdict.';
COMMENT ON COLUMN wiki_claim_grades.grader_version	IS 'Free-form label identifying the grader pass (model + prompt revision).';

CREATE INDEX ix_wiki_claim_grades_claim ON wiki_claim_grades (claim_id);

ALTER TABLE wiki_claim_grades ENABLE ROW LEVEL SECURITY;
ALTER TABLE wiki_claim_grades FORCE  ROW LEVEL SECURITY;
--rollback DROP TABLE IF EXISTS wiki_claim_grades;
