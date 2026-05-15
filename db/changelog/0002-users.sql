--liquibase formatted sql

--changeset ai-librarian:0002-users-1
--comment: Mirror of Microsoft Entra OIDs per ADR 0005. Not an auth store;
--comment: sign-in is performed by Entra and tokens are validated by the API.
CREATE TABLE users (
	id				uuid			PRIMARY KEY,			-- Entra OID
	email			citext			UNIQUE,
	display_name	text,
	is_employee		boolean			NOT NULL DEFAULT true,	-- false for B2B guests
	deactivated_at	timestamptz,
	created_at		timestamptz		NOT NULL DEFAULT now()
);
COMMENT ON TABLE  users					IS 'Mirror of Entra OIDs; one row per known principal.';
COMMENT ON COLUMN users.is_employee		IS 'Distinguishes employees from B2B guests; gates Internal-classification reads (ADR 0005, ADR 0011).';
--rollback DROP TABLE users;

--changeset ai-librarian:0002-users-2
--comment: Sentinel row for system/agent actions referenced by AuditConstants.SystemUserId.
INSERT INTO users (id, display_name, is_employee) VALUES (
	'00000000-0000-0000-0000-00000000ffff',
	'AI Librarian (system)',
	false
) ON CONFLICT (id) DO NOTHING;
--rollback DELETE FROM users WHERE id = '00000000-0000-0000-0000-00000000ffff';

--changeset ai-librarian:0002-users-3
ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE users DISABLE ROW LEVEL SECURITY;
