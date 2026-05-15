--liquibase formatted sql

--changeset ai-librarian:0102-wiki-rls-1
--comment: Phase 2 wiki -- RLS policies for the six wiki tables.
--comment: Predicates follow ADR 0006 + 0011: reads gate on the
--comment: facet's min_classification + the user's department/role
--comment: lattice; writes go through the Wiki Maintainer agent
--comment: (system user) or an Admin / Librarian on the page's
--comment: department.
--comment:
--comment: Reads:
--comment:   wiki_pages  -- visible if you can read at least one of its facets
--comment:   page_facets -- gated on min_classification + dept membership
--comment:   wiki_page_revisions  -- same as page_facets
--comment:   wiki_claims          -- inherits from its parent revision
--comment:   wiki_claim_citations -- inherits from its parent claim
--comment:   wiki_claim_grades    -- inherits from its parent claim
--comment:
--comment: Writes:
--comment:   Admin only (for now). The Phase 2.5 approval queue lets
--comment:   Reviewer/Librarian approve proposed revisions; the Wiki
--comment:   Maintainer agent writes as the system user. Until then,
--comment:   only Admin writes -- no human-direct editing per ADR 0006.

-- ----- wiki_pages -----

CREATE POLICY p_wiki_pages_read ON wiki_pages
	FOR SELECT
	USING (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND department_id = ANY (app_department_ids())
		)
	);

CREATE POLICY p_wiki_pages_write ON wiki_pages
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());

-- ----- page_facets -----

CREATE POLICY p_page_facets_read ON page_facets
	FOR SELECT
	USING (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND EXISTS (
				SELECT 1
				FROM wiki_pages wp
				WHERE wp.id = page_facets.page_id
				  AND wp.department_id = ANY (app_department_ids())
				  -- Classification lattice -- Internal+ requires employee;
				  -- Confidential/Restricted require dept membership of
				  -- the OWNING dept (already covered by department_id =
				  -- ANY app_department_ids() above). Public is open.
				  AND (
					page_facets.min_classification = 'Public'
					OR (
						page_facets.min_classification = 'Internal'
						AND app_is_employee()
					)
					OR page_facets.min_classification IN ('Confidential', 'Restricted')
				  )
			)
		)
	);

CREATE POLICY p_page_facets_write ON page_facets
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());

-- ----- wiki_page_revisions -----

CREATE POLICY p_wiki_page_revisions_read ON wiki_page_revisions
	FOR SELECT
	USING (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND EXISTS (
				SELECT 1
				FROM wiki_pages wp
				WHERE wp.id = wiki_page_revisions.page_id
				  AND wp.department_id = ANY (app_department_ids())
				  AND (
					wiki_page_revisions.min_classification = 'Public'
					OR (
						wiki_page_revisions.min_classification = 'Internal'
						AND app_is_employee()
					)
					OR wiki_page_revisions.min_classification IN ('Confidential', 'Restricted')
				  )
			)
		)
	);

CREATE POLICY p_wiki_page_revisions_write ON wiki_page_revisions
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());

-- ----- wiki_claims -----

CREATE POLICY p_wiki_claims_read ON wiki_claims
	FOR SELECT
	USING (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND EXISTS (
				SELECT 1
				FROM wiki_page_revisions wpr
				INNER JOIN wiki_pages wp ON wp.id = wpr.page_id
				WHERE wpr.id = wiki_claims.revision_id
				  AND wp.department_id = ANY (app_department_ids())
				  AND (
					wiki_claims.facet_classification = 'Public'
					OR (
						wiki_claims.facet_classification = 'Internal'
						AND app_is_employee()
					)
					OR wiki_claims.facet_classification IN ('Confidential', 'Restricted')
				  )
			)
		)
	);

CREATE POLICY p_wiki_claims_write ON wiki_claims
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());

-- ----- wiki_claim_citations -----

CREATE POLICY p_wiki_claim_citations_read ON wiki_claim_citations
	FOR SELECT
	USING (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND EXISTS (
				SELECT 1
				FROM wiki_claims wc
				INNER JOIN wiki_page_revisions wpr ON wpr.id = wc.revision_id
				INNER JOIN wiki_pages wp ON wp.id = wpr.page_id
				WHERE wc.id = wiki_claim_citations.claim_id
				  AND wp.department_id = ANY (app_department_ids())
				  AND (
					wc.facet_classification = 'Public'
					OR (
						wc.facet_classification = 'Internal'
						AND app_is_employee()
					)
					OR wc.facet_classification IN ('Confidential', 'Restricted')
				  )
			)
		)
	);

CREATE POLICY p_wiki_claim_citations_write ON wiki_claim_citations
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());

-- ----- wiki_claim_grades -----

CREATE POLICY p_wiki_claim_grades_read ON wiki_claim_grades
	FOR SELECT
	USING (
		app_is_admin()
		OR (
			app_is_authenticated()
			AND EXISTS (
				SELECT 1
				FROM wiki_claims wc
				INNER JOIN wiki_page_revisions wpr ON wpr.id = wc.revision_id
				INNER JOIN wiki_pages wp ON wp.id = wpr.page_id
				WHERE wc.id = wiki_claim_grades.claim_id
				  AND wp.department_id = ANY (app_department_ids())
				  AND (
					wc.facet_classification = 'Public'
					OR (
						wc.facet_classification = 'Internal'
						AND app_is_employee()
					)
					OR wc.facet_classification IN ('Confidential', 'Restricted')
				  )
			)
		)
	);

CREATE POLICY p_wiki_claim_grades_write ON wiki_claim_grades
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());

--rollback DROP POLICY IF EXISTS p_wiki_claim_grades_write    ON wiki_claim_grades;
--rollback DROP POLICY IF EXISTS p_wiki_claim_grades_read     ON wiki_claim_grades;
--rollback DROP POLICY IF EXISTS p_wiki_claim_citations_write ON wiki_claim_citations;
--rollback DROP POLICY IF EXISTS p_wiki_claim_citations_read  ON wiki_claim_citations;
--rollback DROP POLICY IF EXISTS p_wiki_claims_write          ON wiki_claims;
--rollback DROP POLICY IF EXISTS p_wiki_claims_read           ON wiki_claims;
--rollback DROP POLICY IF EXISTS p_wiki_page_revisions_write  ON wiki_page_revisions;
--rollback DROP POLICY IF EXISTS p_wiki_page_revisions_read   ON wiki_page_revisions;
--rollback DROP POLICY IF EXISTS p_page_facets_write          ON page_facets;
--rollback DROP POLICY IF EXISTS p_page_facets_read           ON page_facets;
--rollback DROP POLICY IF EXISTS p_wiki_pages_write           ON wiki_pages;
--rollback DROP POLICY IF EXISTS p_wiki_pages_read            ON wiki_pages;
