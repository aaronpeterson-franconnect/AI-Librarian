--liquibase formatted sql

--changeset ai-librarian:0005-source-shares-1
--comment: Explicit, auditable, revocable cross-department read grants per ADR 0011.
--comment: A share is the only way for a department other than the owner to read
--comment: a Confidential or Restricted source; Internal does not require a share.
CREATE TABLE source_shares (
	id					uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	source_id			uuid			NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
	grantee_department	uuid			NOT NULL REFERENCES departments(id) ON DELETE CASCADE,
	granted_by			uuid			NOT NULL REFERENCES users(id),
	granted_at			timestamptz		NOT NULL DEFAULT now(),
	revoked_at			timestamptz,
	revoked_by			uuid			REFERENCES users(id),
	reason				text			NOT NULL,
	CONSTRAINT chk_source_shares_revocation
		CHECK (
			(revoked_at IS NULL AND revoked_by IS NULL)
			OR
			(revoked_at IS NOT NULL AND revoked_by IS NOT NULL)
		)
);
COMMENT ON TABLE  source_shares						IS 'Explicit cross-department read grants (ADR 0011).';
COMMENT ON COLUMN source_shares.reason				IS 'Free-text justification; required so reviewers can audit grants without spelunking.';
--rollback DROP TABLE source_shares;

--changeset ai-librarian:0005-source-shares-2
CREATE INDEX ix_source_shares_source	ON source_shares (source_id);
CREATE UNIQUE INDEX ux_source_shares_active
	ON source_shares (source_id, grantee_department)
	WHERE revoked_at IS NULL;
--rollback DROP INDEX IF EXISTS ux_source_shares_active;
--rollback DROP INDEX IF EXISTS ix_source_shares_source;

--changeset ai-librarian:0005-source-shares-3
ALTER TABLE source_shares ENABLE ROW LEVEL SECURITY;
ALTER TABLE source_shares FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE source_shares DISABLE ROW LEVEL SECURITY;
