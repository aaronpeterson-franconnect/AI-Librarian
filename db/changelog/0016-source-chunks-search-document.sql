--liquibase formatted sql

--changeset ai-librarian:0016-source-chunks-search-document-1
--comment: Full-text search column for hybrid retrieval (vector + lexical) per ADR 0001 / architecture.md.
ALTER TABLE source_chunks ADD COLUMN search_document tsvector
	GENERATED ALWAYS AS (to_tsvector('english', coalesce(content_markdown, ''))) STORED;
COMMENT ON COLUMN source_chunks.search_document IS 'English FTS document derived from content_markdown; used with GIN for lexical rank.';
CREATE INDEX ix_source_chunks_search_document ON source_chunks USING gin (search_document);
--rollback DROP INDEX IF EXISTS ix_source_chunks_search_document;
--rollback ALTER TABLE source_chunks DROP COLUMN IF EXISTS search_document;
