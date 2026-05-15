# Operations & Finance Brief — AI Librarian

> Audience: Director of Operations, Finance / FP&A, Cloud Infrastructure lead
> Status: **Draft for review** · Date: 2026-04-29
> Companion documents: [Architecture](../architecture.md) · [Executive summary](../executive-summary.md)

## Why we need your input

AI Librarian's Phase 4 deliverables include cost-control mechanisms
and disaster-recovery posture. Both are configurable but need
defensible default values that match the company's risk appetite
and operational standards. We're proposing concrete numbers below
and asking Operations and Finance to ratify, adjust, or reject.

If you accept our proposals as-stated, we proceed. If you would
adjust, the architecture is designed to accommodate within
reasonable limits — we just need direction on the numbers.

---

## Decision 1 — Cost caps and budgets

### What the system does

LLM spend has four cost components:

1. **Embedding** — one-time per source on ingest; per-query embedding cost
2. **Chat completion** — every user `ask` call; every cascade-regeneration
3. **Reranking** — per query (small fraction of chat-completion cost)
4. **Multimodal** — Azure AI Speech (per audio minute), Azure AI Vision /
   Document Intelligence (per page or image), only for departments with
   media-heavy corpora

We project roughly **$1,500-3,000 / month per department** at steady
state (50-200 active users, normal query volume), based on
public Azure OpenAI pricing for `text-embedding-3-large` and
`gpt-4o`-class chat completion. Heavy ingest months (initial
load, new repo absorption) can spike 2-3x.

### Our proposal

**Tiered budget controls** — per-department, with a system-wide
backstop:

| Threshold | Behavior |
|---|---|
| Below 50% | Normal operation; subtle dashboard indicator |
| **50%** | Department dashboard shows "on track" / "elevated" / "high" status |
| **80%** | Notify Librarian role group; cascade-regeneration jobs deprioritized |
| **100%** | Hard cap — `ask` calls return a polite error pointing the user to their Librarian; ingest queue continues but defers embedding / transcription for new sources |
| Override | Librarian or Admin can request budget increase mid-month; tracked as an audit event |
| Reset | First of each month |

**Per-department default**: **$2,500 / month**. Caps are set per
department in `policy.yaml`; the default is a starting point, not
a commitment.

**System-wide backstop**: **5× sum of department caps**. Absorbs
spikes (e.g., concurrent department onboarding) without runaway
risk.

### What we need from you

1. **Confirm $2,500 / month / department default** as the starting
   number, or set a different one. (We recommend leaving the
   *default* loose so individual departments can be tuned downward
   from the policy file as needed.)
2. **Confirm the system-wide backstop** at 5× sum (or specify
   another multiplier / a hard absolute number).
3. **Confirm the 100% hard-cap behavior**: at cap, user `ask`
   calls fail polite. Acceptable, or should we degrade more gently
   (e.g., switch to a cheaper model)?
4. **Approve the override flow**: Librarian or Admin can grant
   budget increase mid-month with audit trail. Does this need
   Finance pre-approval above a threshold (e.g., budget increases
   over $500 require Finance sign-off)?
5. **Specify the chargeback model**: do departments get charged
   internally for their AI Librarian spend (cost-center allocation),
   or is it an IT shared cost? (Affects how detailed the
   per-department billing reports need to be.)

### Phase relevance

**Phase 4** — cost-cap mechanisms ship in Phase 4. Earlier phases
operate without caps but with full cost telemetry, so we're not
flying blind.

---

## Decision 2 — Disaster recovery RPO / RTO

### What the system does

AI Librarian state lives in:

- **PostgreSQL Flexible Server** — relational data, embeddings, wiki
- **Azure Blob Storage** — immutable raw source bytes (WORM-protected)
- **Azure Service Bus** — ingestion queues (transient; not strictly
  state but loss = work re-queue)
- **Azure Container Apps** — stateless workloads (no DR concern beyond
  redeploy-from-image)
- **Azure Key Vault** — secrets (replicated automatically)
- **Application Insights / Azure Monitor / Sentinel** — telemetry and
  audit (own retention rules)

Two SLOs matter:

- **RPO (Recovery Point Objective)** — how much data we're willing
  to lose
- **RTO (Recovery Time Objective)** — how long full recovery can take

### Our proposal

| Metric | Committed SLA | Likely actual |
|---|---|---|
| **RPO** | **1 hour** | <5 minutes |
| **RTO (same-region failure)** | **4 hours** | 1-2 hours |
| **RTO (cross-region disaster)** | **8 hours** | 4-6 hours |

**How we hit these**:

- **Postgres**: point-in-time recovery (PITR) with continuous WAL
  backup; **35-day backup retention**. Geo-redundant backups
  enabled. Realistic RPO < 5 min; we commit to 1 hour to leave
  margin.
- **Blob Storage**: **GRS (geo-redundant storage)** — secondary
  region copy with ~15 min replication lag. RPO matches.
- **Service Bus**: messages are not state; loss means rework, not
  data loss. Acceptable; queues are repopulated by retry workers.
- **Quarterly DR exercise**: tabletop walk-through and an actual
  restore to an isolated environment, validated against a
  documented runbook.

### What we need from you

1. **Confirm RPO 1 hour** is acceptable, or specify tighter.
   Tighter requires hot read-replica (different cost profile).
2. **Confirm RTO 4 hours (same-region) / 8 hours (cross-region)**
   are acceptable.
3. **Approve 35-day Postgres PITR retention** and **GRS for Blob**.
   Standard for this risk tier; a few hundred dollars/month at
   our projected volume.
4. **Approve the quarterly DR exercise commitment**. Roughly half
   a day of engineering time per quarter plus the test environment
   cost.
5. **Specify the cross-region pairing**. Default proposal:
   **East US 2 (primary) ↔ Central US (paired)**. Confirms with
   any existing IT region-pair conventions.

### Phase relevance

**Phase 4** — DR posture is finalized in Phase 4. Phase 0 deploys
with backups but doesn't necessarily hit the full DR SLAs;
explicit gating in the phase plan.

---

## Summary — what we are asking Operations & Finance to do

1. **Cost caps**: confirm or adjust $2,500/dept/month default,
   5× system backstop, and the 80%/100% behaviors.
2. **Cost overrides**: define the threshold above which Finance
   pre-approval is required for mid-month budget increases.
3. **Chargeback model**: confirm per-department billing detail
   requirements.
4. **DR SLAs**: confirm RPO 1 hour, RTO 4 hours (same-region) /
   8 hours (cross-region).
5. **Backup posture**: approve 35-day Postgres PITR + GRS Blob.
6. **DR exercise cadence**: approve quarterly tabletop + restore
   test.
7. **Region pair**: confirm East US 2 ↔ Central US (or specify a
   different pair if IT prefers).

## Where to read more

- [`../architecture.md`](../architecture.md) — full system blueprint
- [`../phasing.md`](../phasing.md) — six-phase rollout plan
- [`../adr/0001-data-platform-postgres-pgvector.md`](../adr/0001-data-platform-postgres-pgvector.md) — data platform
- [`../open-questions.md`](../open-questions.md) — full open-questions tracker
