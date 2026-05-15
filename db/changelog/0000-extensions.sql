--liquibase formatted sql

--changeset ai-librarian:0000-extensions-1
--comment: Required Postgres extensions. pgvector for embeddings (ADR 0001),
--comment: pg_trgm for trigram lexical match in hybrid retrieval, citext for
--comment: case-insensitive identifiers, pgcrypto for gen_random_uuid().
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
--rollback DROP EXTENSION IF EXISTS pgcrypto;
--rollback DROP EXTENSION IF EXISTS citext;
--rollback DROP EXTENSION IF EXISTS pg_trgm;
--rollback DROP EXTENSION IF EXISTS vector;
