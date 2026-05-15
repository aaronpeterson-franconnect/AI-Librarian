--liquibase formatted sql

--changeset ai-librarian:0012-persona-action-records-1
--comment: Per-action records of internal autonomous activity per ADR 0016.
--comment: Every recommend / shadow / autonomous decision lands here; the
--comment: human-review outcome lands in persona_action_outcomes alongside.
CREATE TABLE persona_action_records (
	id				uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	occurred_at		timestamptz		NOT NULL DEFAULT now(),
	persona_id		uuid			NOT NULL REFERENCES personas(id),
	actor_user_id	uuid			NOT NULL REFERENCES users(id),
	action_type		text			NOT NULL,
	mode			text			NOT NULL,
	target_kind		text			NOT NULL,
	target_id		uuid			NOT NULL,
	proposed_value	jsonb			NOT NULL,
	prior_value		jsonb,
	confidence		numeric(3,2)	NOT NULL,
	correlation_id	uuid			NOT NULL,
	committed		boolean			NOT NULL DEFAULT false,
	reversed_at		timestamptz,
	reversed_by		uuid			REFERENCES users(id),
	reversal_reason	text,
	CONSTRAINT chk_persona_action_mode
		CHECK (mode IN ('recommend','shadow','autonomous')),
	CONSTRAINT chk_persona_action_confidence
		CHECK (confidence BETWEEN 0 AND 1),
	CONSTRAINT chk_persona_action_reversal
		CHECK (
			(reversed_at IS NULL AND reversed_by IS NULL AND reversal_reason IS NULL)
			OR
			(reversed_at IS NOT NULL AND reversed_by IS NOT NULL AND reversal_reason IS NOT NULL)
		)
);
COMMENT ON TABLE  persona_action_records					IS 'Internal autonomous-action ledger (ADR 0016); reversibility-by-design.';
COMMENT ON COLUMN persona_action_records.actor_user_id		IS 'AuditConstants.SystemUserId for autonomous mode; the human user for recommend / shadow.';
COMMENT ON COLUMN persona_action_records.action_type		IS 'Persona-specific verb, e.g. "ticket.classify", "ticket.route".';
COMMENT ON COLUMN persona_action_records.committed			IS 'True only when the action effected a real-world change (autonomous mode).';
--rollback DROP TABLE persona_action_records;

--changeset ai-librarian:0012-persona-action-records-2
CREATE INDEX ix_persona_action_records_persona		ON persona_action_records (persona_id, occurred_at DESC);
CREATE INDEX ix_persona_action_records_action		ON persona_action_records (action_type, mode, occurred_at DESC);
CREATE INDEX ix_persona_action_records_target		ON persona_action_records (target_kind, target_id);
CREATE INDEX ix_persona_action_records_correlation	ON persona_action_records (correlation_id);
--rollback DROP INDEX IF EXISTS ix_persona_action_records_correlation;
--rollback DROP INDEX IF EXISTS ix_persona_action_records_target;
--rollback DROP INDEX IF EXISTS ix_persona_action_records_action;
--rollback DROP INDEX IF EXISTS ix_persona_action_records_persona;

--changeset ai-librarian:0012-persona-action-records-3
ALTER TABLE persona_action_records ENABLE ROW LEVEL SECURITY;
ALTER TABLE persona_action_records FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE persona_action_records DISABLE ROW LEVEL SECURITY;
