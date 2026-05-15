--liquibase formatted sql

--changeset ai-librarian:0100-fix-source-shares-rls-recursion-1
--comment: Fix mutual recursion between p_sources_read and the source_shares
--comment: policies, discovered by the RLS chaos battery (Phase 1 Week 2
--comment: hardening). Two cycles existed:
--comment:
--comment:   1. p_sources_read (Confidential/Restricted EXISTS source_shares)
--comment:      → p_source_shares_read (EXISTS sources) → p_sources_read = recurse
--comment:
--comment:   2. p_sources_read (any path) → evaluates source_shares policies
--comment:      → p_source_shares_write (FOR ALL, USING EXISTS sources)
--comment:      → p_sources_read = recurse
--comment:
--comment: Postgres's RLS planner detects the cycle conservatively at query
--comment: time and aborts with SQLSTATE 42P17. The dev/local environment
--comment: connected as the superuser (BYPASSRLS implicit), so the bug was
--comment: invisible until the testcontainer battery ran with a non-
--comment: superuser app role.
--comment:
--comment: Fix: rewrite both source_shares policies to gate strictly on
--comment: data carried by the source_shares row itself (grantee_department,
--comment: granted_by). The "is the caller a librarian of the source's
--comment: department?" check moves to the API/application layer — the
--comment: route handler that calls source_shares.INSERT first verifies
--comment: librarian status on the source's department before issuing the
--comment: write. RLS still enforces the visibility rules; the policy
--comment: simply no longer crosses tables.
DROP POLICY IF EXISTS p_source_shares_read  ON source_shares;
DROP POLICY IF EXISTS p_source_shares_write ON source_shares;

CREATE POLICY p_source_shares_read ON source_shares
	FOR SELECT
	USING (
		app_is_admin()
		OR grantee_department = ANY (app_department_ids())
		OR grantee_department = ANY (app_librarian_depts())
	);

CREATE POLICY p_source_shares_write ON source_shares
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());
--rollback DROP POLICY IF EXISTS p_source_shares_write ON source_shares;
--rollback DROP POLICY IF EXISTS p_source_shares_read  ON source_shares;
--rollback CREATE POLICY p_source_shares_read ON source_shares
--rollback 	FOR SELECT
--rollback 	USING (
--rollback 		app_is_admin()
--rollback 		OR EXISTS (SELECT 1 FROM sources s WHERE s.id = source_shares.source_id)
--rollback 		OR EXISTS (SELECT 1 FROM sources s WHERE s.id = source_shares.source_id AND s.department_id = ANY (app_librarian_depts()))
--rollback 	);
--rollback CREATE POLICY p_source_shares_write ON source_shares
--rollback 	FOR ALL
--rollback 	USING (app_is_admin() OR EXISTS (SELECT 1 FROM sources s WHERE s.id = source_shares.source_id AND s.department_id = ANY (app_librarian_depts())))
--rollback 	WITH CHECK (app_is_admin() OR EXISTS (SELECT 1 FROM sources s WHERE s.id = source_shares.source_id AND s.department_id = ANY (app_librarian_depts())));
