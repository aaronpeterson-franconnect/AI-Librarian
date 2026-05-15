--liquibase formatted sql

--changeset ai-librarian:0004-sources-1
--comment: Source corpus per ADR 0005 (department-owned) and ADR 0011 (classification).
--comment: Classification is the default access boundary; department + role govern writes.
CREATE TABLE sources (
	id					uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	department_id		uuid			NOT NULL REFERENCES departments(id),
	classification		text			NOT NULL DEFAULT 'Internal',
	title				text			NOT NULL,
	uri					text,
	content_type		text			NOT NULL,
	checksum_sha256		text,
	size_bytes			bigint,
	contributed_by		uuid			NOT NULL REFERENCES users(id),
	approved_by			uuid			REFERENCES users(id),
	approved_at			timestamptz,
	soft_deleted_at		timestamptz,
	created_at			timestamptz		NOT NULL DEFAULT now(),
	updated_at			timestamptz		NOT NULL DEFAULT now(),
	CONSTRAINT chk_sources_classification
		CHECK (classification IN ('Public','Internal','Confidential','Restricted'))
);
COMMENT ON TABLE  sources					IS 'Department-owned source artifacts; classification gates read access (ADR 0011).';
COMMENT ON COLUMN sources.classification	IS 'One of Public, Internal, Confidential, Restricted; default Internal per ADR 0011.';
COMMENT ON COLUMN sources.soft_deleted_at	IS 'Tier-1 deletion per ADR 0008; row hidden from RLS but kept for audit.';
--rollback DROP TABLE sources;

--changeset ai-librarian:0004-sources-2
CREATE INDEX ix_sources_department		ON sources (department_id);
CREATE INDEX ix_sources_classification	ON sources (classification);
CREATE INDEX ix_sources_active
	ON sources (department_id, classification)
	WHERE soft_deleted_at IS NULL;
--rollback DROP INDEX IF EXISTS ix_sources_active;
--rollback DROP INDEX IF EXISTS ix_sources_classification;
--rollback DROP INDEX IF EXISTS ix_sources_department;

--changeset ai-librarian:0004-sources-3
ALTER TABLE sources ENABLE ROW LEVEL SECURITY;
ALTER TABLE sources FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE sources DISABLE ROW LEVEL SECURITY;
