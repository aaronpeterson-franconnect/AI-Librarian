--liquibase formatted sql

--changeset ai-librarian:0030-wiki-proposed-revisions-1
--comment: Phase 2.5 approval queue per ADR 0006. When wiki_pages.locked
--comment: is true, the Wiki Maintainer writes a proposed revision into
--comment: this table instead of committing directly to
--comment: wiki_page_revisions. A Reviewer or Librarian on the page's
--comment: department then accepts or rejects via the admin endpoints.
--comment:
--comment: Schema notes:
--comment:   - proposed_payload is JSONB carrying the would-be claims +
--comment:     citations. Storing here (vs. in wiki_claims with a state
--comment:     column) keeps wiki_claims immutable per ADR 0007. On
--comment:     accept, the API copies the payload into a new
--comment:     wiki_page_revisions + wiki_claims + wiki_claim_citations
--comment:     transaction.
--comment:   - expires_at defaults to now() + 14 days per ADR 0006 Q13.
--comment:     A periodic sweep transitions stale pending proposals to
--comment:     state='expired' with reason 'expired without review'.
--comment:   - state machine: pending -> accepted | rejected | expired.
--comment:     Once non-pending, decided_by + decided_at are required.

CREATE TABLE wiki_proposed_revisions (
	id							uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	page_id						uuid			NOT NULL REFERENCES wiki_pages(id) ON DELETE CASCADE,
	min_classification			text			NOT NULL,
	persona_id					uuid			REFERENCES personas(id) ON DELETE RESTRICT,
	proposed_revision_number	int				NOT NULL,
	authored_by					uuid			NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
	authored_at					timestamptz		NOT NULL DEFAULT now(),
	expires_at					timestamptz		NOT NULL DEFAULT (now() + interval '14 days'),
	body_markdown				text			NOT NULL DEFAULT '',
	proposed_payload			jsonb			NOT NULL,
	state						text			NOT NULL DEFAULT 'pending',
	decided_by					uuid			REFERENCES users(id) ON DELETE RESTRICT,
	decided_at					timestamptz		,
	decision_reason				text			,

	CONSTRAINT chk_wiki_proposed_revisions_classification
		CHECK (min_classification IN ('Public','Internal','Confidential','Restricted')),

	CONSTRAINT chk_wiki_proposed_revisions_state
		CHECK (state IN ('pending','accepted','rejected','expired')),

	-- Pending: decided_by + decided_at MUST be null. Non-pending: both required.
	CONSTRAINT chk_wiki_proposed_revisions_decision_pair
		CHECK (
			(state = 'pending' AND decided_by IS NULL AND decided_at IS NULL)
			OR (state <> 'pending' AND decided_by IS NOT NULL AND decided_at IS NOT NULL)
		),

	CONSTRAINT chk_wiki_proposed_revisions_revno
		CHECK (proposed_revision_number >= 1)
);

COMMENT ON TABLE  wiki_proposed_revisions IS 'Phase 2.5 approval queue per ADR 0006. Locked-page edits land here until a Reviewer/Librarian decides.';
COMMENT ON COLUMN wiki_proposed_revisions.proposed_payload IS 'JSONB carrying the claims + citations the maintainer would have committed. Schema: { "claims": [{ "text": str, "position": int, "citations": [{ "chunk_id": uuid, "span_start": int, "span_end": int, "confidence": number }] }] }';
COMMENT ON COLUMN wiki_proposed_revisions.expires_at IS 'Auto-rejection deadline. ADR 0006 Q13 default = 14 days.';

CREATE INDEX ix_wiki_proposed_revisions_page ON wiki_proposed_revisions (page_id);
CREATE INDEX ix_wiki_proposed_revisions_state ON wiki_proposed_revisions (state) WHERE state = 'pending';
CREATE INDEX ix_wiki_proposed_revisions_expires ON wiki_proposed_revisions (expires_at) WHERE state = 'pending';

-- Allow at most ONE pending proposal per facet at a time. If a fresh
-- maintenance run produces a new proposal while an older one is still
-- pending, the maintainer should either reject the old one or skip
-- the new one -- handled application-side via this index surfacing the
-- conflict as 23505.
CREATE UNIQUE INDEX ux_wiki_proposed_revisions_pending_per_facet
	ON wiki_proposed_revisions (page_id, min_classification, COALESCE(persona_id, '00000000-0000-0000-0000-000000000000'::uuid))
	WHERE state = 'pending';

ALTER TABLE wiki_proposed_revisions ENABLE ROW LEVEL SECURITY;
ALTER TABLE wiki_proposed_revisions FORCE  ROW LEVEL SECURITY;
--rollback DROP TABLE IF EXISTS wiki_proposed_revisions;
