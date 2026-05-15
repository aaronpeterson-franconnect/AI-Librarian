--liquibase formatted sql

--changeset ai-librarian:0001-departments-1
--comment: Flat departments per ADR 0005. No parent/child hierarchy.
CREATE TABLE departments (
	id				uuid			PRIMARY KEY DEFAULT gen_random_uuid(),
	name			citext			NOT NULL UNIQUE,
	display_name	text			NOT NULL,
	deactivated_at	timestamptz,
	created_at		timestamptz		NOT NULL DEFAULT now()
);
COMMENT ON TABLE  departments			IS 'Flat departments per ADR 0005; corpus-ownership unit.';
COMMENT ON COLUMN departments.name		IS 'Lowercased machine name (citext); stable across renames.';
--rollback DROP TABLE departments;

--changeset ai-librarian:0001-departments-2
--comment: Enable RLS so the table follows the same predicate model as the rest.
ALTER TABLE departments ENABLE ROW LEVEL SECURITY;
ALTER TABLE departments FORCE  ROW LEVEL SECURITY;
--rollback ALTER TABLE departments DISABLE ROW LEVEL SECURITY;
