--liquibase formatted sql

--changeset ai-librarian:0101-users-self-provisioning-1
--comment: Permit JIT user provisioning per ADR 0005. Before this changeset,
--comment: p_users_write was Admin-only -- meaning a first-time Entra sign-in
--comment: had no users row, no FK target for audit_events.actor_user_id, and
--comment: no path to RLS-resolve the caller. Adding two narrow self-targeted
--comment: policies lets the API insert / update the caller's own users row
--comment: when EnsureUserAsync runs at session-context build time. Inserting
--comment: someone else's row stays Admin-only via p_users_write (the policies
--comment: are OR-combined for permissive policies).
--comment:
--comment: Security note: the WITH CHECK predicate locks the row's id to the
--comment: caller's app.user_id, so a user can only ever insert / update the
--comment: single row that represents themselves. is_employee and other
--comment: attributes are application-supplied; RLS does not validate them
--comment: (the app code derives is_employee from the bearer's idtyp/acct
--comment: claims, which Entra signs).

CREATE POLICY p_users_self_insert ON users
	FOR INSERT
	WITH CHECK (
		app_is_authenticated()
		AND id = app_user_id()
	);

CREATE POLICY p_users_self_update ON users
	FOR UPDATE
	USING (
		app_is_authenticated()
		AND id = app_user_id()
	)
	WITH CHECK (
		app_is_authenticated()
		AND id = app_user_id()
	);
--rollback DROP POLICY IF EXISTS p_users_self_update ON users;
--rollback DROP POLICY IF EXISTS p_users_self_insert ON users;
