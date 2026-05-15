--liquibase formatted sql

--changeset ai-librarian:0103-wiki-proposed-revisions-rls-1
--comment: RLS for wiki_proposed_revisions per ADR 0006.
--comment:   - Read: Admin OR Reviewer/Librarian on the page's department
--comment:     (so reviewers see proposals queued for review).
--comment:   - Insert: Admin only (the Wiki Maintainer runs as system).
--comment:   - Update: Admin OR Reviewer/Librarian on the page's
--comment:     department (so they can transition pending -> accepted
--comment:     /rejected). Application-layer constraints enforce the
--comment:     state-machine; RLS is the access boundary.
--comment:   - Delete: Admin only. Proposals are an audit-relevant
--comment:     history; cascade only when the parent page is deleted.

CREATE POLICY p_wiki_proposed_revisions_read ON wiki_proposed_revisions
	FOR SELECT
	USING (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND EXISTS (
				SELECT 1 FROM wiki_pages wp
				WHERE wp.id = wiki_proposed_revisions.page_id
				  AND (
					wp.department_id = ANY (app_reviewer_depts())
					OR wp.department_id = ANY (app_librarian_depts())
				  )
			)
		)
	);

CREATE POLICY p_wiki_proposed_revisions_insert ON wiki_proposed_revisions
	FOR INSERT
	WITH CHECK (app_is_admin());

CREATE POLICY p_wiki_proposed_revisions_update ON wiki_proposed_revisions
	FOR UPDATE
	USING (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND EXISTS (
				SELECT 1 FROM wiki_pages wp
				WHERE wp.id = wiki_proposed_revisions.page_id
				  AND (
					wp.department_id = ANY (app_reviewer_depts())
					OR wp.department_id = ANY (app_librarian_depts())
				  )
			)
		)
	)
	WITH CHECK (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND EXISTS (
				SELECT 1 FROM wiki_pages wp
				WHERE wp.id = wiki_proposed_revisions.page_id
				  AND (
					wp.department_id = ANY (app_reviewer_depts())
					OR wp.department_id = ANY (app_librarian_depts())
				  )
			)
		)
	);

CREATE POLICY p_wiki_proposed_revisions_delete ON wiki_proposed_revisions
	FOR DELETE
	USING (app_is_admin());

--rollback DROP POLICY IF EXISTS p_wiki_proposed_revisions_delete ON wiki_proposed_revisions;
--rollback DROP POLICY IF EXISTS p_wiki_proposed_revisions_update ON wiki_proposed_revisions;
--rollback DROP POLICY IF EXISTS p_wiki_proposed_revisions_insert ON wiki_proposed_revisions;
--rollback DROP POLICY IF EXISTS p_wiki_proposed_revisions_read   ON wiki_proposed_revisions;
