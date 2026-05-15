--liquibase formatted sql

--changeset ai-librarian:0028-sources-source-type-1
--comment: Add the persona-level source-type taxonomy per ADR 0015
--comment: §"Retrieval-profile shape". The persona reranker applies
--comment: sourceTypeWeights against this column; values not in the
--comment: documented set are rejected by the check constraint so a
--comment: typo in the ingestion classifier surfaces as a 23514 at
--comment: write time rather than silently dropping out of the
--comment: weight map at read time.
--comment:
--comment: Nullable on purpose: existing rows have no taxonomy entry,
--comment: and the reranker treats NULL as "no opinion" -- the same as
--comment: a populated value with no weight in the profile. Backfilling
--comment: the historical corpus is a future operational task; the
--comment: column is here so new ingestion can start populating.
ALTER TABLE sources
	ADD COLUMN source_type text;

ALTER TABLE sources
	ADD CONSTRAINT chk_sources_source_type
		CHECK (
			source_type IS NULL
			OR source_type IN (
				'code',
				'sql',
				'runbook',
				'ticket',
				'meeting_transcript',
				'wiki_page',
				'email',
				'image',
				'document'
			)
		);

COMMENT ON COLUMN sources.source_type IS 'Persona-level taxonomy per ADR 0015. NULL = unclassified (treated as no-opinion by the reranker). Populated by the ingestion classifier; check constraint enforces the documented vocabulary.';

CREATE INDEX ix_sources_source_type
	ON sources (source_type)
	WHERE source_type IS NOT NULL;
--rollback DROP INDEX IF EXISTS ix_sources_source_type;
--rollback ALTER TABLE sources DROP CONSTRAINT IF EXISTS chk_sources_source_type;
--rollback ALTER TABLE sources DROP COLUMN IF EXISTS source_type;
