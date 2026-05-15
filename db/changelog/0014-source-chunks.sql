--liquibase formatted sql



--changeset ai-librarian:0014-source-chunks-1

--comment: Retrieval chunks derived from sources per ADR 0009 (canonical markdown + span anchors).

CREATE TABLE source_chunks (

	id					uuid			PRIMARY KEY DEFAULT gen_random_uuid(),

	source_id			uuid			NOT NULL REFERENCES sources(id) ON DELETE CASCADE,

	order_index			int				NOT NULL,

	content_markdown	text			NOT NULL,

	span_anchor			jsonb			NOT NULL,

	created_at			timestamptz		NOT NULL DEFAULT now(),

	CONSTRAINT uq_source_chunks_order UNIQUE (source_id, order_index)

);

COMMENT ON TABLE source_chunks IS 'Paragraph-level (or finer) chunks produced by skill canonicalization; span_anchor is format-specific JSON (ADR 0009).';

CREATE INDEX ix_source_chunks_source ON source_chunks (source_id);

ALTER TABLE source_chunks ENABLE ROW LEVEL SECURITY;

ALTER TABLE source_chunks FORCE ROW LEVEL SECURITY;

--rollback DROP TABLE IF EXISTS source_chunks;


