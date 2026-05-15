--liquibase formatted sql

--changeset ai-librarian:0104-wiki-pages-soft-delete-rls-1
--comment: Update wiki_pages read policy to hide soft-deleted rows.
--comment: Mirrors the sources read policy (0099): soft_deleted_at IS
--comment: NULL is at the TOP of the USING clause so it applies to
--comment: every branch including app_is_admin(). Soft-deleted pages
--comment: are invisible to RLS callers (operators with direct DB
--comment: access can still see them for audit + undo).
--comment:
--comment: Downstream tables (page_facets, wiki_page_revisions,
--comment: wiki_claims, wiki_claim_citations) read through EXISTS
--comment: clauses against wiki_pages -- once the page is invisible,
--comment: those rows naturally disappear from RLS-filtered reads too.
DROP POLICY IF EXISTS p_wiki_pages_read ON wiki_pages;
CREATE POLICY p_wiki_pages_read ON wiki_pages
	FOR SELECT
	USING (
		soft_deleted_at IS NULL
		AND (
			app_is_admin()
			OR (
				app_is_authenticated()
				AND department_id = ANY (app_department_ids())
			)
		)
	);
--rollback DROP POLICY IF EXISTS p_wiki_pages_read ON wiki_pages;
--rollback CREATE POLICY p_wiki_pages_read ON wiki_pages
--rollback 	FOR SELECT
--rollback 	USING (
--rollback 		app_is_admin()
--rollback 		OR (
--rollback 			app_is_authenticated()
--rollback 			AND department_id = ANY (app_department_ids())
--rollback 		)
--rollback 	);
