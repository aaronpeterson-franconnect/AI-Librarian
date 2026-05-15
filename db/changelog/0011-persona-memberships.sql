--liquibase formatted sql

--changeset ai-librarian:0011-persona-memberships-1
--comment: A user's grant of a persona, optionally department-scoped and
--comment: optionally time-bounded. Per ADR 0014. Write authority for
--comment: this table is per the ADR 0005 amendment: Admin globally,
--comment: Librarian-of-the-department for department-scoped grants.
--comment:
--comment: department_id is nullable so we use a surrogate id PK plus
--comment: two partial unique indexes (one for global, one for scoped)
--comment: to enforce one membership per (user, persona, optional dept).
CREATE TABLE persona_memberships (
	id				uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	user_id			uuid			NOT NULL REFERENCES users(id) ON DELETE CASCADE,
	persona_id		uuid			NOT NULL REFERENCES personas(id) ON DELETE CASCADE,
	department_id	uuid			REFERENCES departments(id) ON DELETE CASCADE,	-- NULL = any-department scope
	granted_at		timestamptz		NOT NULL DEFAULT now(),
	expires_at		timestamptz,
	granted_by		uuid			NOT NULL REFERENCES users(id)
);
COMMENT ON TABLE  persona_memberships					IS 'User grants of personas (ADR 0014); not consulted by RLS read predicates.';
COMMENT ON COLUMN persona_memberships.department_id		IS 'Optional scope; NULL means the persona applies regardless of department context.';
COMMENT ON COLUMN persona_memberships.expires_at		IS 'Optional time bound; NULL means open-ended.';
--rollback DROP TABLE persona_memberships;

--changeset ai-librarian:0011-persona-memberships-2
--comment: Uniqueness — one global membership per (user, persona) when
--comment: department_id is NULL, and one scoped membership per
--comment: (user, persona, department) when it is set.
CREATE UNIQUE INDEX ux_persona_membership_global
	ON persona_memberships (user_id, persona_id)
	WHERE department_id IS NULL;

CREATE UNIQUE INDEX ux_persona_membership_scoped
	ON persona_memberships (user_id, persona_id, department_id)
	WHERE department_id IS NOT NULL;
--rollback DROP INDEX IF EXISTS ux_persona_membership_scoped;
--rollback DROP INDEX IF EXISTS ux_persona_membership_global;

--changeset ai-librarian:0011-persona-memberships-3
CREATE INDEX ix_persona_membership_user		ON persona_memberships (user_id);
CREATE INDEX ix_persona_membership_persona	ON persona_memberships (persona_id);
CREATE INDEX ix_persona_membership_dept		ON persona_memberships (department_id);
--rollback DROP INDEX IF EXISTS ix_persona_membership_dept;
--rollback DROP INDEX IF EXISTS ix_persona_membership_persona;
--rollback DROP INDEX IF EXISTS ix_persona_membership_user;

--changeset ai-librarian:0011-persona-memberships-4
ALTER TABLE persona_memberships ENABLE ROW LEVEL SECURITY;
ALTER TABLE persona_memberships FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE persona_memberships DISABLE ROW LEVEL SECURITY;
