--liquibase formatted sql

--changeset ai-librarian:0003-user-authorizations-1
--comment: Materialized Entra group membership per ADR 0005.
--comment: Each row is a (user, role, optional department) grant driven by
--comment: a specific Entra group. Admin is system-wide and has department_id NULL.
CREATE TABLE user_authorizations (
	id				uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	user_id			uuid			NOT NULL REFERENCES users(id) ON DELETE CASCADE,
	department_id	uuid			REFERENCES departments(id) ON DELETE CASCADE,	-- NULL for Admin
	role			text			NOT NULL,
	source_group_id	text			NOT NULL,
	granted_at		timestamptz		NOT NULL DEFAULT now(),
	CONSTRAINT chk_user_auth_role
		CHECK (role IN ('Reader','Contributor','Reviewer','Librarian','Admin')),
	CONSTRAINT chk_user_auth_admin_no_dept
		CHECK (
			(role  = 'Admin' AND department_id IS NULL)
			OR
			(role <> 'Admin' AND department_id IS NOT NULL)
		)
);
COMMENT ON TABLE  user_authorizations					IS 'Materialized Entra group membership; one row per (user, dept, role) or per (user, Admin).';
COMMENT ON COLUMN user_authorizations.source_group_id	IS 'The Entra group object id that produced this grant; audit anchor.';
--rollback DROP TABLE user_authorizations;

--changeset ai-librarian:0003-user-authorizations-2
--comment: Uniqueness — Admin is unique per user and role; non-Admin per (user, dept, role).
CREATE UNIQUE INDEX ux_user_auth_admin
	ON user_authorizations (user_id, role)
	WHERE department_id IS NULL;

CREATE UNIQUE INDEX ux_user_auth_dept_role
	ON user_authorizations (user_id, department_id, role)
	WHERE department_id IS NOT NULL;

CREATE INDEX ix_user_auth_dept   ON user_authorizations (department_id);
CREATE INDEX ix_user_auth_user   ON user_authorizations (user_id);
--rollback DROP INDEX IF EXISTS ix_user_auth_user;
--rollback DROP INDEX IF EXISTS ix_user_auth_dept;
--rollback DROP INDEX IF EXISTS ux_user_auth_dept_role;
--rollback DROP INDEX IF EXISTS ux_user_auth_admin;

--changeset ai-librarian:0003-user-authorizations-3
ALTER TABLE user_authorizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_authorizations FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE user_authorizations DISABLE ROW LEVEL SECURITY;
