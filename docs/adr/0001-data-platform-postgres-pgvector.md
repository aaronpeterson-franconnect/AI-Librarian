# ADR 0001 — Use Azure Database for PostgreSQL Flexible Server with `pgvector` as the single data spine

> Status: **Accepted** · Date: 2026-04-29 · Deciders: Architect (initial proposal — to be ratified)

## Context

AI Librarian needs to store, in a transactionally consistent way:

1. Relational entities (users, user authorizations, departments,
   sources, source shares, chunks, wiki pages, page facets, page
   revisions, claims, citations, audit events)
2. Vector embeddings for semantic search (one per chunk; estimated
   ~1M chunks at year-1 medium scale; year-2/3 trajectory in the
   table below)
3. Classification-driven read access combined with role-based write
   authority enforced at the data layer (per ADR 0005, ADR 0011)

We expect medium scale in year 1 (10–25 departments, 100–500 users,
~1M documents). We are an Azure-first, .NET shop with established
PostgreSQL operational practice. Our selected scope (audit, citations,
RLS, deletion) demands transactional consistency between relational
data, vectors, and access control.

**Scale trajectory note (2026-04-29 revision).** Engineering's
intended use cases include ingesting whole code repositories and
transcribing every Teams meeting. Realistic chunk-count projection:

| Source | Year 1 | Year 2 | Year 3 |
|---|---|---|---|
| Code (semantic chunks per function/class) | 1.0–1.5M | 2.0–3.0M | 3.0–4.5M |
| Meeting transcripts (~50 chunks per meeting) | 1.0–1.3M | 2.0–2.5M | 3.0–4.0M |
| Documents and other | 0.3–0.5M | 0.6–1.0M | 1.0–1.5M |
| **Total** | **~3–5M** | **~6–10M** | **~10–15M** |

`pgvector` with HNSW handles year 1 comfortably. Year 2 lands in the
upper half of its comfort zone. Year 3 reaches the ceiling on a
single instance — partitioning helps; migration to a dedicated
vector store may be needed.

## Decision

We use a **single Azure Database for PostgreSQL Flexible Server** with
the following extensions:

- `pgvector` (HNSW index) for embeddings
- `pgcrypto` for hashing helpers
- Native partitioning (range, monthly) for the audit ledger

We do **not** introduce a separate vector database, a separate graph
database, or a separate audit store at this stage.

Bytes (raw blobs of submitted sources) live in **Azure Blob Storage**
with WORM (immutability) policies; Postgres stores only metadata,
chunks (canonicalized markdown), embeddings, and wiki content.

## Consequences

### Easier

- One transaction can update relational data, embeddings, audit log,
  and wiki claims atomically. RTBF cascades become straightforward.
- RLS policies sit in one place and cover everything, vectors included.
- Operational story is one we already know (PITR, geo-replication,
  Defender for SQL).
- One backup and restore strategy.
- Authentication via Entra-integrated Postgres roles is supported
  natively.

### Harder

- `pgvector` performance ceiling is real. With HNSW and proper tuning
  it scales comfortably to ~10–20M vectors on a moderately-sized
  instance, but beyond that we will need partitioning by department or
  a dedicated vector store (Qdrant / Weaviate / Azure AI Search).
- Heavy concurrent vector queries can compete with OLTP traffic. We
  will run a read replica for retrieval workloads at Phase 4.
- We forgo specialized graph queries that a Neo4j-style store offers.
  We can model the wiki link graph in Postgres (recursive CTEs over
  citation tables); if graph queries become a hot path, we revisit.

### Risks

- **Hitting the vector ceiling in year 2/3** given the projected
  growth. Concrete mitigation plan:
  1. **From Phase 1**: partition the `embeddings` table by
     `department_id` from the start. This is additive, costs nothing
     if we don't need it, and makes per-department index builds
     tractable later.
  2. **From Phase 3**: implement an embedding lifecycle policy.
     Meeting transcripts older than N months and code chunks for
     superseded git revisions move out of the hot vector index but
     remain in raw storage and full-text search. Reduces the active
     vector count without losing coverage.
  3. **In Phase 4**: scheduled scale evaluation. Metrics: p95
     vector-search latency, HNSW rebuild duration per partition,
     index size per department, query mix. Concrete migration
     triggers documented in open question Q18.
  4. **Migration target**: Azure AI Search (Microsoft-native, lives
     in our tenant, integrates with Entra) is the primary candidate
     if pgvector hits its ceiling. Qdrant is a backup.

## Alternatives considered

### Postgres + dedicated vector store (Qdrant / Weaviate / Azure AI Search)

Better vector scale and metadata filtering, but introduces a second
data store and a consistency problem (RLS, cascades, audit must span
both). Not justified at our year-1 scale.

### Multi-database split (Postgres + Cosmos DB + Azure AI Search)

Three operational stories, three backup strategies, three RLS-equivalent
mechanisms to maintain. Net negative for an enterprise app at this
scale.

### Azure SQL Database

Strong relational story and Microsoft-native, but vector support is
behind `pgvector` in maturity. The .NET ecosystem speaks Postgres
fluently (Npgsql, EF Core, Liquibase).

### Supabase (managed Postgres + auth + storage)

OB1's choice. Excellent developer experience but introduces a SaaS
dependency and is a poor fit for our enterprise data-residency posture.

## References

- [Open Brain — Row Level Security primitive](https://github.com/NateBJones-Projects/OB1/tree/main/primitives/rls)
- [Azure Database for PostgreSQL pgvector docs](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/how-to-use-pgvector)
- [pgvector HNSW index notes](https://github.com/pgvector/pgvector#hnsw)
- ADR 0005 — Role-based RLS with Entra
- ADR 0007 — Claim-level citation contract
- ADR 0010 — Audit ledger
- ADR 0013 — Hyperscaler deployment scope (Postgres + pgvector chosen partly for cross-hyperscaler portability — same engine on Azure Postgres Flexible Server and AWS RDS / Aurora)
