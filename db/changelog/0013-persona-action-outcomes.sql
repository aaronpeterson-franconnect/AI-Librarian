--liquibase formatted sql

--changeset ai-librarian:0013-persona-action-outcomes-1
--comment: Human-evaluated outcome for a persona action per ADR 0016.
--comment: Drives the agreement-with-human metric that gates promotion
--comment: through the recommend -> shadow -> autonomous progression.
CREATE TABLE persona_action_outcomes (
	id					uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	action_record_id	uuid			NOT NULL REFERENCES persona_action_records(id) ON DELETE CASCADE,
	evaluated_at		timestamptz		NOT NULL DEFAULT now(),
	evaluated_by		uuid			NOT NULL REFERENCES users(id),
	human_decision		jsonb			NOT NULL,
	agreement			text			NOT NULL,
	notes				text,
	CONSTRAINT chk_persona_outcome_agreement
		CHECK (agreement IN ('agree','disagree','partial','indeterminate'))
);
COMMENT ON TABLE  persona_action_outcomes				IS 'Human evaluation of a persona action; powers the agreement metric (ADR 0016).';
COMMENT ON COLUMN persona_action_outcomes.human_decision IS 'The decision the human actually made or would have made; jsonb to keep schema-flexible across action types.';
--rollback DROP TABLE persona_action_outcomes;

--changeset ai-librarian:0013-persona-action-outcomes-2
CREATE UNIQUE INDEX ux_persona_outcome_per_record
	ON persona_action_outcomes (action_record_id);
CREATE INDEX ix_persona_outcome_evaluator
	ON persona_action_outcomes (evaluated_by, evaluated_at DESC);
--rollback DROP INDEX IF EXISTS ix_persona_outcome_evaluator;
--rollback DROP INDEX IF EXISTS ux_persona_outcome_per_record;

--changeset ai-librarian:0013-persona-action-outcomes-3
ALTER TABLE persona_action_outcomes ENABLE ROW LEVEL SECURITY;
ALTER TABLE persona_action_outcomes FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE persona_action_outcomes DISABLE ROW LEVEL SECURITY;
