--liquibase formatted sql
--
-- All RLS predicates centralized so reviewers see the access model in one place.
-- Per ADR 0005 (flat departments + roles), ADR 0011 (classification as default
-- access boundary), ADR 0014 (persona is NOT a visibility dimension).
--
-- Session variables consulted are exactly the set pushed by
-- AiLibrarian.Infrastructure.Rls.RlsSessionPusher. app.persona_id is set on
-- every transaction but is intentionally NEVER consulted by an RLS predicate.
--

--changeset ai-librarian:0099-rls-helpers-1 splitStatements:false stripComments:false
--comment: Helper functions to read app.* session variables in policy bodies.
--comment: splitStatements:false because the multiple plpgsql function bodies
--comment: below each contain semicolons inside $$ ... $$ delimiters which
--comment: Liquibase's default parser would split on, breaking the bodies apart.
CREATE OR REPLACE FUNCTION app_uuid_array(setting_name text)
RETURNS uuid[] AS $$
DECLARE
	raw text;
BEGIN
	raw := current_setting(setting_name, true);
	IF raw IS NULL OR raw = '' THEN
		RETURN ARRAY[]::uuid[];
	END IF;
	RETURN string_to_array(raw, ',')::uuid[];
END;
$$ LANGUAGE plpgsql STABLE;

CREATE OR REPLACE FUNCTION app_bool(setting_name text)
RETURNS boolean AS $$
DECLARE
	raw text;
BEGIN
	raw := current_setting(setting_name, true);
	RETURN COALESCE(raw::boolean, false);
END;
$$ LANGUAGE plpgsql STABLE;

CREATE OR REPLACE FUNCTION app_uuid(setting_name text)
RETURNS uuid AS $$
DECLARE
	raw text;
BEGIN
	raw := current_setting(setting_name, true);
	IF raw IS NULL OR raw = '' THEN
		RETURN NULL;
	END IF;
	RETURN raw::uuid;
END;
$$ LANGUAGE plpgsql STABLE;

CREATE OR REPLACE FUNCTION app_is_admin() RETURNS boolean AS $$
	SELECT app_bool('app.is_admin');
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION app_is_employee() RETURNS boolean AS $$
	SELECT app_bool('app.is_employee');
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION app_is_authenticated() RETURNS boolean AS $$
	SELECT app_bool('app.is_authenticated');
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION app_user_id() RETURNS uuid AS $$
	SELECT app_uuid('app.user_id');
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION app_department_ids() RETURNS uuid[] AS $$
	SELECT app_uuid_array('app.department_ids');
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION app_librarian_depts() RETURNS uuid[] AS $$
	SELECT app_uuid_array('app.librarian_depts');
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION app_reviewer_depts() RETURNS uuid[] AS $$
	SELECT app_uuid_array('app.reviewer_depts');
$$ LANGUAGE sql STABLE;

CREATE OR REPLACE FUNCTION app_contributor_depts() RETURNS uuid[] AS $$
	SELECT app_uuid_array('app.contributor_depts');
$$ LANGUAGE sql STABLE;
--rollback DROP FUNCTION IF EXISTS app_contributor_depts();
--rollback DROP FUNCTION IF EXISTS app_reviewer_depts();
--rollback DROP FUNCTION IF EXISTS app_librarian_depts();
--rollback DROP FUNCTION IF EXISTS app_department_ids();
--rollback DROP FUNCTION IF EXISTS app_user_id();
--rollback DROP FUNCTION IF EXISTS app_is_authenticated();
--rollback DROP FUNCTION IF EXISTS app_is_employee();
--rollback DROP FUNCTION IF EXISTS app_is_admin();
--rollback DROP FUNCTION IF EXISTS app_uuid(text);
--rollback DROP FUNCTION IF EXISTS app_bool(text);
--rollback DROP FUNCTION IF EXISTS app_uuid_array(text);

--changeset ai-librarian:0099-rls-departments-1
--comment: Departments — every authenticated principal can list; only Admin writes.
CREATE POLICY p_departments_read ON departments
	FOR SELECT
	USING (app_is_authenticated());

CREATE POLICY p_departments_write ON departments
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());
--rollback DROP POLICY IF EXISTS p_departments_write ON departments;
--rollback DROP POLICY IF EXISTS p_departments_read ON departments;

--changeset ai-librarian:0099-rls-users-1
--comment: Users — Admin-only writes; reads scoped to authenticated principals.
CREATE POLICY p_users_read ON users
	FOR SELECT
	USING (app_is_authenticated());

CREATE POLICY p_users_write ON users
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());
--rollback DROP POLICY IF EXISTS p_users_write ON users;
--rollback DROP POLICY IF EXISTS p_users_read ON users;

--changeset ai-librarian:0099-rls-user-authorizations-1
--comment: User authorizations — self-read, Admin sees everything, Librarian
--comment: sees their department. Writes are Admin-only per ADR 0005.
CREATE POLICY p_user_auth_read ON user_authorizations
	FOR SELECT
	USING (
		user_id = app_user_id()
		OR app_is_admin()
		OR (department_id IS NOT NULL AND department_id = ANY (app_librarian_depts()))
	);

CREATE POLICY p_user_auth_write ON user_authorizations
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());
--rollback DROP POLICY IF EXISTS p_user_auth_write ON user_authorizations;
--rollback DROP POLICY IF EXISTS p_user_auth_read ON user_authorizations;

--changeset ai-librarian:0099-rls-sources-1
--comment: Sources — the heart of the access model per ADR 0011.
--comment: Public is world-readable; Internal requires employee; Confidential
--comment: and Restricted require department membership or an active source_share.
--comment: Soft-deleted rows are hidden (ADR 0008).
CREATE POLICY p_sources_read ON sources
	FOR SELECT
	USING (
		soft_deleted_at IS NULL
		AND (
			classification = 'Public'
			OR (
				classification = 'Internal'
				AND app_is_employee()
			)
			OR (
				classification = 'Confidential'
				AND (
					department_id = ANY (app_department_ids())
					OR app_is_admin()
					OR EXISTS (
						SELECT 1 FROM source_shares ss
						WHERE ss.source_id = sources.id
							AND ss.revoked_at IS NULL
							AND ss.grantee_department = ANY (app_department_ids())
					)
				)
			)
			OR (
				classification = 'Restricted'
				AND (
					department_id = ANY (app_librarian_depts())
					OR app_is_admin()
					OR EXISTS (
						SELECT 1 FROM source_shares ss
						WHERE ss.source_id = sources.id
							AND ss.revoked_at IS NULL
							AND ss.grantee_department = ANY (app_librarian_depts())
					)
				)
			)
		)
	);

--comment: Writes — Contributor or higher can INSERT into their department;
--comment: Reviewer or higher can UPDATE; Librarian or higher can soft-delete.
--comment: Phase 0 ships a coarse "Contributor of department" predicate; per-
--comment: column DML privileges (e.g., who can flip approved_at) are layered
--comment: in Phase 1 via per-action UPDATE policies.
CREATE POLICY p_sources_write ON sources
	FOR ALL
	USING (
		app_is_admin()
		OR department_id = ANY (app_contributor_depts())
	)
	WITH CHECK (
		app_is_admin()
		OR department_id = ANY (app_contributor_depts())
	);
--rollback DROP POLICY IF EXISTS p_sources_write ON sources;
--rollback DROP POLICY IF EXISTS p_sources_read  ON sources;

--changeset ai-librarian:0099-rls-source-shares-1
--comment: Source shares — readable when you can read the underlying source
--comment: OR you're a Librarian of the granting department. Writes are
--comment: Librarian-of-the-granting-department or Admin.
CREATE POLICY p_source_shares_read ON source_shares
	FOR SELECT
	USING (
		app_is_admin()
		OR EXISTS (SELECT 1 FROM sources s WHERE s.id = source_shares.source_id)
		OR EXISTS (
			SELECT 1 FROM sources s
			WHERE s.id = source_shares.source_id
				AND s.department_id = ANY (app_librarian_depts())
		)
	);

CREATE POLICY p_source_shares_write ON source_shares
	FOR ALL
	USING (
		app_is_admin()
		OR EXISTS (
			SELECT 1 FROM sources s
			WHERE s.id = source_shares.source_id
				AND s.department_id = ANY (app_librarian_depts())
		)
	)
	WITH CHECK (
		app_is_admin()
		OR EXISTS (
			SELECT 1 FROM sources s
			WHERE s.id = source_shares.source_id
				AND s.department_id = ANY (app_librarian_depts())
		)
	);
--rollback DROP POLICY IF EXISTS p_source_shares_write ON source_shares;
--rollback DROP POLICY IF EXISTS p_source_shares_read  ON source_shares;

--changeset ai-librarian:0099-rls-audit-events-1
--comment: Audit events — Admin sees all; principals see their own activity;
--comment: Librarians see their department's activity. Writes are
--comment: append-only and gated by the no-mutate trigger from changeset
--comment: 0006-audit-events-3 in addition to the policy below.
CREATE POLICY p_audit_events_read ON audit_events
	FOR SELECT
	USING (
		app_is_admin()
		OR actor_user_id = app_user_id()
		OR (department_id IS NOT NULL AND department_id = ANY (app_librarian_depts()))
	);

CREATE POLICY p_audit_events_insert ON audit_events
	FOR INSERT
	WITH CHECK (app_is_authenticated());
--rollback DROP POLICY IF EXISTS p_audit_events_insert ON audit_events;
--rollback DROP POLICY IF EXISTS p_audit_events_read   ON audit_events;

--changeset ai-librarian:0099-rls-personas-1
--comment: Personas — readable to any authenticated principal (display);
--comment: Admin-only writes. Persona is NOT a visibility dimension; this
--comment: read predicate is intentionally identical to other reference data.
CREATE POLICY p_personas_read ON personas
	FOR SELECT
	USING (app_is_authenticated());

CREATE POLICY p_personas_write ON personas
	FOR ALL
	USING (app_is_admin())
	WITH CHECK (app_is_admin());
--rollback DROP POLICY IF EXISTS p_personas_write ON personas;
--rollback DROP POLICY IF EXISTS p_personas_read  ON personas;

--changeset ai-librarian:0099-rls-persona-memberships-1
--comment: Persona memberships per the ADR 0005 amendment:
--comment:   READ:  self, Admin, or Librarian of a scoped department.
--comment:   WRITE: Admin globally; department-scoped grants writable by
--comment:         a Librarian of that department.
CREATE POLICY p_persona_memberships_read ON persona_memberships
	FOR SELECT
	USING (
		user_id = app_user_id()
		OR app_is_admin()
		OR (department_id IS NOT NULL AND department_id = ANY (app_librarian_depts()))
	);

CREATE POLICY p_persona_memberships_write ON persona_memberships
	FOR ALL
	USING (
		app_is_admin()
		OR (department_id IS NOT NULL AND department_id = ANY (app_librarian_depts()))
	)
	WITH CHECK (
		app_is_admin()
		OR (department_id IS NOT NULL AND department_id = ANY (app_librarian_depts()))
	);
--rollback DROP POLICY IF EXISTS p_persona_memberships_write ON persona_memberships;
--rollback DROP POLICY IF EXISTS p_persona_memberships_read  ON persona_memberships;

--changeset ai-librarian:0099-rls-persona-action-records-1 splitStatements:false stripComments:false
--comment: Persona action records -- Admin sees all; the actor sees their own;
--comment: persona-Sponsoring Owners see records for their persona (read).
--comment: Phase 0 ships a basic predicate; Sponsoring-Owner role lands in Phase 1.
--comment: splitStatements:false because the persona_action_records_only_reversal_columns()
--comment: function body contains semicolons inside $$ ... $$ delimiters which
--comment: Liquibase's default parser would split on.
CREATE POLICY p_persona_action_records_read ON persona_action_records
	FOR SELECT
	USING (
		app_is_admin()
		OR actor_user_id = app_user_id()
	);

CREATE POLICY p_persona_action_records_insert ON persona_action_records
	FOR INSERT
	WITH CHECK (app_is_authenticated());

--comment: UPDATE is allowed only on the reversal columns; enforced via a
--comment: trigger because Postgres RLS does not support per-column USING.
--comment: The trigger raises if any non-reversal column is changed.
CREATE OR REPLACE FUNCTION persona_action_records_only_reversal_columns()
RETURNS trigger AS $$
BEGIN
	IF (NEW.id, NEW.occurred_at, NEW.persona_id, NEW.actor_user_id, NEW.action_type,
	    NEW.mode, NEW.target_kind, NEW.target_id, NEW.proposed_value, NEW.prior_value,
	    NEW.confidence, NEW.correlation_id, NEW.committed)
	   IS DISTINCT FROM
	   (OLD.id, OLD.occurred_at, OLD.persona_id, OLD.actor_user_id, OLD.action_type,
	    OLD.mode, OLD.target_kind, OLD.target_id, OLD.proposed_value, OLD.prior_value,
	    OLD.confidence, OLD.correlation_id, OLD.committed)
	THEN
		RAISE EXCEPTION 'persona_action_records: only reversal columns may be updated (ADR 0016)';
	END IF;
	RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_persona_action_records_immutable
	BEFORE UPDATE ON persona_action_records
	FOR EACH ROW EXECUTE FUNCTION persona_action_records_only_reversal_columns();

CREATE POLICY p_persona_action_records_update ON persona_action_records
	FOR UPDATE
	USING (
		app_is_admin()
		OR actor_user_id = app_user_id()
	)
	WITH CHECK (
		app_is_admin()
		OR actor_user_id = app_user_id()
	);
--rollback DROP POLICY IF EXISTS p_persona_action_records_update ON persona_action_records;
--rollback DROP TRIGGER IF EXISTS trg_persona_action_records_immutable ON persona_action_records;
--rollback DROP FUNCTION IF EXISTS persona_action_records_only_reversal_columns();
--rollback DROP POLICY IF EXISTS p_persona_action_records_insert ON persona_action_records;
--rollback DROP POLICY IF EXISTS p_persona_action_records_read   ON persona_action_records;

--changeset ai-librarian:0099-rls-persona-action-outcomes-1
--comment: Outcomes follow the same visibility as the action they evaluate;
--comment: writes require an authenticated evaluator (Librarian or higher of
--comment: the relevant department). Phase 0 ships a basic predicate.
CREATE POLICY p_persona_action_outcomes_read ON persona_action_outcomes
	FOR SELECT
	USING (
		app_is_admin()
		OR EXISTS (
			SELECT 1 FROM persona_action_records par
			WHERE par.id = persona_action_outcomes.action_record_id
				AND (par.actor_user_id = app_user_id() OR app_is_admin())
		)
	);

CREATE POLICY p_persona_action_outcomes_write ON persona_action_outcomes
	FOR ALL
	USING (app_is_authenticated())
	WITH CHECK (app_is_authenticated() AND evaluated_by = app_user_id());
--rollback DROP POLICY IF EXISTS p_persona_action_outcomes_write ON persona_action_outcomes;
--rollback DROP POLICY IF EXISTS p_persona_action_outcomes_read  ON persona_action_outcomes;

--changeset ai-librarian:0099-rls-source-chunks-1
--comment: Chunks follow the same read rules as the parent source; writes require
--comment: Contributor+ on the source department (mirrors p_sources_write).
CREATE POLICY p_source_chunks_read ON source_chunks
	FOR SELECT
	USING (
		EXISTS (
			SELECT 1
			FROM sources s
			WHERE s.id = source_chunks.source_id
				AND s.soft_deleted_at IS NULL
				AND (
					s.classification = 'Public'
					OR (
						s.classification = 'Internal'
						AND app_is_employee()
					)
					OR (
						s.classification = 'Confidential'
						AND (
							s.department_id = ANY (app_department_ids())
							OR app_is_admin()
							OR EXISTS (
								SELECT 1 FROM source_shares ss
								WHERE ss.source_id = s.id
									AND ss.revoked_at IS NULL
									AND ss.grantee_department = ANY (app_department_ids())
							)
						)
					)
					OR (
						s.classification = 'Restricted'
						AND (
							s.department_id = ANY (app_librarian_depts())
							OR app_is_admin()
							OR EXISTS (
								SELECT 1 FROM source_shares ss
								WHERE ss.source_id = s.id
									AND ss.revoked_at IS NULL
									AND ss.grantee_department = ANY (app_librarian_depts())
							)
						)
					)
				)
		)
	);

CREATE POLICY p_source_chunks_write ON source_chunks
	FOR ALL
	USING (
		EXISTS (
			SELECT 1 FROM sources s
			WHERE s.id = source_chunks.source_id
				AND (
					app_is_admin()
					OR s.department_id = ANY (app_contributor_depts())
				)
		)
	)
	WITH CHECK (
		EXISTS (
			SELECT 1 FROM sources s
			WHERE s.id = source_chunks.source_id
				AND (
					app_is_admin()
					OR s.department_id = ANY (app_contributor_depts())
				)
		)
	);
--rollback DROP POLICY IF EXISTS p_source_chunks_write ON source_chunks;
--rollback DROP POLICY IF EXISTS p_source_chunks_read  ON source_chunks;
