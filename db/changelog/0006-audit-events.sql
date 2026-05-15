--liquibase formatted sql

--changeset ai-librarian:0006-audit-events-1
--comment: Single append-only audit ledger per ADR 0010, partitioned by month.
--comment: This is the parent declarative-partition table; child partitions are
--comment: created by the partition-maintenance job (Phase 0 ships a manual
--comment: bootstrap of partitions for the next 12 months).
CREATE TABLE audit_events (
	id					uuid			NOT NULL DEFAULT gen_random_uuid(),
	occurred_at			timestamptz		NOT NULL DEFAULT now(),
	actor_user_id		uuid			NOT NULL REFERENCES users(id),
	actor_role			text,
	originated_by		uuid			REFERENCES users(id),
	department_id		uuid			REFERENCES departments(id),
	event_type			text			NOT NULL,
	event_subtype		text,
	target_kind			text,
	target_id			uuid,
	correlation_id		uuid			NOT NULL,
	outcome				text			NOT NULL,
	error_class			text,
	llm_provider		text,
	llm_model			text,
	llm_prompt_tokens	int,
	llm_completion_tokens int,
	llm_cost_usd		numeric(12,6),
	llm_latency_ms		int,
	llm_persona_id		uuid,
	details				jsonb			NOT NULL DEFAULT '{}'::jsonb,
	CONSTRAINT chk_audit_outcome CHECK (outcome IN ('Success','Failure','Partial')),
	CONSTRAINT pk_audit_events   PRIMARY KEY (occurred_at, id)
) PARTITION BY RANGE (occurred_at);
COMMENT ON TABLE  audit_events					IS 'Append-only SOC2-grade audit ledger per ADR 0010; partitioned by occurred_at month.';
COMMENT ON COLUMN audit_events.actor_user_id	IS 'AuditConstants.SystemUserId (00000000-0000-0000-0000-00000000ffff) for autonomous agent actions.';
COMMENT ON COLUMN audit_events.llm_persona_id	IS 'Persona under which the LLM call ran (ADR 0007 amendment + ADR 0014).';
COMMENT ON COLUMN audit_events.details			IS 'Structured detail; never includes raw prompt or completion text (ADR 0010 content-capture policy).';
--rollback DROP TABLE audit_events;

--changeset ai-librarian:0006-audit-events-2 splitStatements:false stripComments:false
--comment: Bootstrap monthly partitions for the next 12 months; the
--comment: partition-maintenance job (Phase 1) keeps a rolling window.
--comment: splitStatements:false is required because Liquibase's default
--comment: SQL parser splits on every `;` and would cut the DO $$ ... $$
--comment: block apart at the first semicolon inside the PL/pgSQL body.
DO $$
DECLARE
	month_start date := date_trunc('month', current_date)::date;
	month_end   date;
	part_name   text;
	i           int;
BEGIN
	FOR i IN 0..11 LOOP
		month_start := (date_trunc('month', current_date) + make_interval(months => i))::date;
		month_end   := (date_trunc('month', current_date) + make_interval(months => i + 1))::date;
		part_name   := format('audit_events_%s', to_char(month_start, 'YYYY_MM'));
		EXECUTE format(
			'CREATE TABLE IF NOT EXISTS %I PARTITION OF audit_events FOR VALUES FROM (%L) TO (%L);',
			part_name, month_start, month_end
		);
	END LOOP;
END $$;
--rollback -- partitions are dropped when the parent table is dropped.

--changeset ai-librarian:0006-audit-events-3 splitStatements:false stripComments:false
--comment: Append-only enforcement: deny UPDATE and DELETE with a row-level trigger.
--comment: Belt-and-suspenders behind the RLS write predicates in 0099.
--comment: splitStatements:false because the function body contains semicolons
--comment: inside its $$ ... $$ delimiters which Liquibase's default parser splits on.
CREATE OR REPLACE FUNCTION audit_events_no_mutate() RETURNS trigger AS $$
BEGIN
	RAISE EXCEPTION 'audit_events is append-only (ADR 0010); % blocked', TG_OP;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_audit_events_no_update
	BEFORE UPDATE ON audit_events
	FOR EACH ROW EXECUTE FUNCTION audit_events_no_mutate();

CREATE TRIGGER trg_audit_events_no_delete
	BEFORE DELETE ON audit_events
	FOR EACH ROW EXECUTE FUNCTION audit_events_no_mutate();
--rollback DROP TRIGGER IF EXISTS trg_audit_events_no_delete ON audit_events;
--rollback DROP TRIGGER IF EXISTS trg_audit_events_no_update ON audit_events;
--rollback DROP FUNCTION IF EXISTS audit_events_no_mutate();

--changeset ai-librarian:0006-audit-events-4
CREATE INDEX ix_audit_correlation		ON audit_events (correlation_id);
CREATE INDEX ix_audit_actor				ON audit_events (actor_user_id, occurred_at DESC);
CREATE INDEX ix_audit_event_type		ON audit_events (event_type, event_subtype, occurred_at DESC);
CREATE INDEX ix_audit_target			ON audit_events (target_kind, target_id);
--rollback DROP INDEX IF EXISTS ix_audit_target;
--rollback DROP INDEX IF EXISTS ix_audit_event_type;
--rollback DROP INDEX IF EXISTS ix_audit_actor;
--rollback DROP INDEX IF EXISTS ix_audit_correlation;

--changeset ai-librarian:0006-audit-events-5
ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE audit_events DISABLE ROW LEVEL SECURITY;
