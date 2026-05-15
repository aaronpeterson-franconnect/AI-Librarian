--liquibase formatted sql

--changeset ai-librarian:0010-personas-1
--comment: Personas — fourth organizing dimension per ADR 0014. Persona
--comment: shapes retrieval, synthesis, and persona-action authority; it
--comment: is intentionally absent from every RLS read predicate.
CREATE TABLE personas (
	id						uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	name					citext			NOT NULL UNIQUE,
	display_name			text			NOT NULL,
	description				text			NOT NULL,
	retrieval_profile		jsonb			NOT NULL DEFAULT '{}'::jsonb,
	synthesis_style			jsonb			NOT NULL DEFAULT '{}'::jsonb,
	default_action_set		jsonb			NOT NULL DEFAULT '[]'::jsonb,
	classification_floor	text			NOT NULL DEFAULT 'Internal',
	deactivated_at			timestamptz,
	created_at				timestamptz		NOT NULL DEFAULT now(),
	CONSTRAINT chk_persona_classification_floor
		CHECK (classification_floor IN ('Public','Internal','Confidential','Restricted'))
);
COMMENT ON TABLE  personas						IS 'Persona definitions per ADR 0014. NOT a visibility dimension.';
COMMENT ON COLUMN personas.retrieval_profile	IS 'Source-tier weights, recency-decay, persona-specific signals (ADR 0015).';
COMMENT ON COLUMN personas.synthesis_style		IS 'Tone, format, abstain rules, citation density (ADR 0015).';
COMMENT ON COLUMN personas.default_action_set	IS 'Action types available for this persona; per-action mode is on persona_action_records (ADR 0016).';
COMMENT ON COLUMN personas.classification_floor	IS 'Lowest classification this persona surfaces in retrieval; narrows retrieval, never visibility.';
--rollback DROP TABLE personas;

--changeset ai-librarian:0010-personas-2
ALTER TABLE personas ENABLE ROW LEVEL SECURITY;
ALTER TABLE personas FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE personas DISABLE ROW LEVEL SECURITY;
