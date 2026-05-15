--liquibase formatted sql

--changeset ai-librarian:0015-source-chunks-embedding-1
--comment: Chunk embeddings for hybrid retrieval (ADR 0001). Dimension matches Azure OpenAI text-embedding-3-small / ada-002 class models (1536).
ALTER TABLE source_chunks ADD COLUMN embedding vector(1536);
ALTER TABLE source_chunks ADD COLUMN embedding_model text;
ALTER TABLE source_chunks ADD COLUMN embedded_at timestamptz;
COMMENT ON COLUMN source_chunks.embedding IS 'Dense embedding; dimension must match IngestWorker:Embeddings:ExpectedDimensions and the deployment.';
CREATE INDEX ix_source_chunks_embedding_hnsw ON source_chunks
	USING hnsw (embedding vector_cosine_ops)
	WITH (m = 16, ef_construction = 64)
	WHERE embedding IS NOT NULL;
--rollback DROP INDEX IF EXISTS ix_source_chunks_embedding_hnsw;
--rollback ALTER TABLE source_chunks DROP COLUMN IF EXISTS embedding_model;
--rollback ALTER TABLE source_chunks DROP COLUMN IF EXISTS embedded_at;
--rollback ALTER TABLE source_chunks DROP COLUMN IF EXISTS embedding;
