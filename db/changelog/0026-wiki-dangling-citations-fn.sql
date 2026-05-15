--liquibase formatted sql

--changeset ai-librarian:0026-wiki-dangling-citations-fn-1 splitStatements:false stripComments:false
--comment: Phase 2 wiki -- dangling-citation detector function from the
--comment: pre-Phase-1 hardening plan item #4. Returns (claim_id,
--comment: citation_id, reason) tuples for citations whose underlying
--comment: source_chunk is missing (hard-delete) or soft-deleted
--comment: (source row's soft_deleted_at is non-null).
--comment:
--comment: department_id NULL means "every department the caller can
--comment: read." since NULL means "since the beginning of time." Both
--comment: defaults match the Phase 4 Cascade-Regeneration Worker's
--comment: expected call shape ("everything that may have gone
--comment: dangling in the last hour").
--comment:
--comment: The function is SECURITY INVOKER (default) so it respects
--comment: the caller's RLS. The wiki tables ENABLE FORCE ROW LEVEL
--comment: SECURITY -- a caller can only see dangling citations on
--comment: claims they can read.
CREATE OR REPLACE FUNCTION audit_dangling_citations(
	p_department_id	uuid			DEFAULT NULL,
	p_since			timestamptz		DEFAULT NULL
)
RETURNS TABLE (
	claim_id		uuid,
	citation_id		uuid,
	chunk_id		uuid,
	reason			text
)
LANGUAGE sql
STABLE
AS $$
	SELECT
		wcc.claim_id,
		wcc.id AS citation_id,
		wcc.chunk_id,
		CASE
			WHEN sc.id IS NULL THEN 'chunk_missing'
			WHEN s.soft_deleted_at IS NOT NULL THEN 'source_soft_deleted'
			ELSE 'unknown'
		END AS reason
	FROM wiki_claim_citations wcc
	INNER JOIN wiki_claims wc ON wc.id = wcc.claim_id
	INNER JOIN wiki_page_revisions wpr ON wpr.id = wc.revision_id
	INNER JOIN wiki_pages wp ON wp.id = wpr.page_id
	LEFT  JOIN source_chunks sc ON sc.id = wcc.chunk_id
	LEFT  JOIN sources s ON s.id = sc.source_id
	WHERE
		(p_department_id IS NULL OR wp.department_id = p_department_id)
		AND (p_since IS NULL OR wcc.created_at >= p_since)
		AND (
			sc.id IS NULL					-- hard-deleted chunk
			OR s.soft_deleted_at IS NOT NULL	-- soft-deleted source
		)
	ORDER BY wcc.claim_id, wcc.id;
$$;

COMMENT ON FUNCTION audit_dangling_citations IS 'Per pre-Phase-1 hardening plan #4 + ADR 0007. Returns dangling wiki_claim_citations for a department since a timestamp. SECURITY INVOKER -- the caller''s RLS context bounds the result.';
--rollback DROP FUNCTION IF EXISTS audit_dangling_citations(uuid, timestamptz);
