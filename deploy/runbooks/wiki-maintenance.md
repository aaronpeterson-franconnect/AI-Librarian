# Wiki maintenance — operational guide

> Phase 2 wiki schema lands in migrations 0020-0026 + 0102. The Wiki
> Maintainer (`src/AiLibrarian.WikiMaintainer/`) produces revisions
> from approved sources via a two-pass LLM pipeline; this runbook is
> how operators trigger maintenance and how the periodic worker stays
> healthy.

## Three ways to trigger maintenance

> **Don't know what pages to create yet?** Use the
> cluster-based candidate-discovery endpoint described in the
> "Cluster-based candidate discovery" section below. It samples the
> department corpus and proposes named pages — operators eyeball the
> list and call `/discover` for the ones they like.

### 1. Auto-page-discovery (recommended for first-time pages)

The one-shot path. Submit a department, title, classification, and
topic; the API creates `wiki_pages` + `page_facets` rows on first call
(idempotent on repeats), then runs the same maintenance pass as
endpoint 2. Use this for any page that doesn't exist yet — no SQL
required.

```powershell
$token = "<bearer for Admin caller>"
$body = @{
    departmentId        = "<engineering-dept-guid>"
    title               = "Ingest Worker"
    # slug = "ingest-worker"          # optional; derived from title if omitted
    facetClassification = "Internal"
    personaId           = $null
    topic               = "How the ingest worker boots and consumes Service Bus messages"
} | ConvertTo-Json -Compress

Invoke-RestMethod `
    -Method Post `
    -Uri "https://<api-host>/api/admin/wiki/discover" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" `
    -Body $body
```

Response shape:

```json
{
    "pageId": "...",
    "slug": "ingest-worker",
    "pageCreated": true,
    "facetCreated": true,
    "maintenance": {
        "succeeded": true,
        "revisionId": "...",
        "revisionNumber": 1,
        "claimCount": 12,
        "citationCount": 18,
        "chunkPoolSize": 20,
        "rejectionReason": null
    }
}
```

Idempotency contract:

- Same `(departmentId, slug)` → reuses the existing page; `pageCreated=false`. **Title is never overwritten** — rename pages by other means.
- Same `(pageId, facetClassification, personaId)` → reuses the existing facet; `facetCreated=false`.
- Different `facetClassification` or different `personaId` on the same page → adds a new facet additively; the page stays single-row.

Slug rules: must match `^[a-z0-9][a-z0-9\-]{0,254}$` (the
`wiki_pages.slug` check constraint). The endpoint derives a slug from
`title` when omitted (lowercases, strips diacritics, replaces runs of
non-alphanumeric with `-`, trims). Supply `slug` explicitly when the
derived value is wrong.

Audit row `wiki/discover.ok` (or `wiki/discover.rejected`) carries
both the ensure-page outcome and the maintenance result in a single
event — the operator's action is one logical step.

### 2. On-demand against an existing page

```powershell
$token = "<bearer for Admin caller>"
$body = @{
    pageId              = "11111111-1111-1111-1111-111111111111"
    facetClassification = "Internal"
    personaId           = $null    # or a GUID for a persona facet
    topic               = "How the ingest worker boots and consumes Service Bus messages"
} | ConvertTo-Json -Compress

Invoke-RestMethod `
    -Method Post `
    -Uri "https://<api-host>/api/admin/wiki/maintain" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" `
    -Body $body
```

Response shape:

```json
{
    "succeeded": true,
    "revisionId": "...",
    "revisionNumber": 7,
    "claimCount": 12,
    "citationCount": 18,
    "chunkPoolSize": 20,
    "rejectionReason": null
}
```

This endpoint requires the `pageId` to already exist (and the
matching `page_facets` row). For brand-new pages, prefer endpoint 1
(`/api/admin/wiki/discover`) which materializes them as part of the
same call. The maintainer deliberately doesn't decide what pages
should exist — that's a librarian decision per ADR 0006.

### 3. Cascade-Regeneration Worker (periodic)

The `WikiMaintenanceHostedService` runs inside the API process. Off
by default; flip on with:

```jsonc
"WikiMaintenance": {
    "CascadeRegenerationEnabled": true,
    "Interval": "01:00:00",
    "RetrievalLimit": 20,
    "HybridVectorWeight": 0.6,
    "MaxFacetsPerCascadeTick": 10
}
```

Each tick:

1. Calls `audit_dangling_citations(department=NULL, since=NULL)` —
   the SQL function from migration 0026 returns every dangling
   citation across every department.
2. Groups dangling citations by their parent facet
   `(page_id, classification, persona_id)` so the regeneration unit
   is one facet, not one citation.
3. For each affected facet (up to `MaxFacetsPerCascadeTick`):
   - Pulls a fresh source pool via `IHybridChunkSearch` using the
     page title as the retrieval query.
   - Computes the next `revision_number` (max+1).
   - Calls `IWikiMaintainer.GenerateRevisionAsync`.
   - Emits a `wiki/regen.facet` audit row with the outcome.
4. Emits a `wiki/regen.tick` audit row with per-tick totals.

`MaxFacetsPerCascadeTick` is the safety valve. After a big
soft-delete event many facets may go dangling at once; the cap keeps
the worker from monopolizing the LLM budget.

## How to know it's working

### Successful revision

```
info: AiLibrarian.WikiMaintainer.WikiMaintainer[0]
      Wiki revision committed page=<...> facet=Internal revno=3
      revision=<...> claims=12 duration_ms=4720
```

Audit row `wiki/maintain.ok` (manual) or `wiki/regen.facet` (cascade)
with `outcome=Success`.

### Rejected revision

The maintainer never lands a partial revision. When validation
rejects (any of ADR 0007's rules 1-5), the response carries a
`rejectionReason`:

```json
{
  "succeeded": false,
  "revisionId": null,
  "rejectionReason": "Validation failed: 3 violation(s) across 2 claim(s). First few: [R1.ClaimHasCitation@1:Claim has no citations.] [R4.ClassificationNotLeaking@2:Cited chunk classification Confidential exceeds facet ceiling Internal.] ..."
}
```

The most common rejections:

| Rule | Trigger | Fix |
|------|--------|------|
| R1 | LLM emitted a sentence without a `[chunk:...]` token | Re-run; tune the system prompt; reduce temperature |
| R2 | Citation points at a chunk that was soft-deleted between retrieval and validation | Re-run after the source review settles |
| R3 | Citation span out of chunk bounds | Almost always a bug in `WikiMaintainerOptions.DefaultCitationSpanLength` vs. tiny chunks; raise the option or trim the chunk pool |
| R4 | Cited chunk's classification exceeds the facet ceiling | Tighten the source pool query; tune the system prompt's classification framing |
| R5 | Citation confidence below floor | Lower `Quality:CitationValidator:ConfidenceFloor` or raise `WikiMaintainer:DefaultCitationConfidence` |

## Audit shape

| EventType | EventSubtype | Emitted when |
|---|---|---|
| `wiki` | `discover.candidates` | Cluster-based candidate discovery returned a candidate batch |
| `wiki` | `discover.ok` | Auto-page-discovery endpoint succeeded |
| `wiki` | `discover.rejected` | Auto-page-discovery endpoint rejected (rule failure / commit error) |
| `wiki` | `maintain.ok` | On-demand endpoint succeeded |
| `wiki` | `maintain.rejected` | On-demand endpoint rejected (rule failure / commit error) |
| `wiki` | `page.renamed` | PATCH `/pages/{id}` updated the title |
| `wiki` | `page.locked` | PATCH `/pages/{id}` flipped the locked flag |
| `wiki` | `page.soft_deleted` | DELETE `/pages/{id}` soft-deleted the page (ADR 0008 Tier-1) |
| `wiki` | `page.restored` | POST `/pages/{id}/restore` cleared the soft-delete (un-delete) |
| `wiki` | `proposal.bulk_rejected` | Bulk-reject endpoint completed; details carry rejected/skipped/not-found counts |
| `wiki` | `regen.facet` | Cascade worker handled one facet |
| `wiki` | `regen.tick` | Cascade worker finished one periodic tick |

All are `AuditCriticality.Critical` — silent failure here defeats the
cascade and governance contracts from ADR 0006 / 0007.

## Cluster-based candidate discovery

For the "what pages should this department have?" question, the
discovery endpoint samples chunks, clusters them by semantic
similarity, and asks the LLM to name each cluster. The result is a
list of candidate pages the operator can selectively materialize.

```powershell
$token = "<bearer for Admin caller>"
$body = @{
    departmentId   = "<dept-guid>"
    sampleSize     = 100   # optional, default 100, clamped to [5, 500]
    maxCandidates  = 5     # optional, default 5, clamped to [1, 20]
} | ConvertTo-Json -Compress

Invoke-RestMethod -Method Post `
    -Uri "https://<api-host>/api/admin/wiki/discover-candidates" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" -Body $body
```

Response shape:
```json
{
    "sampledChunkCount": 100,
    "embeddingDeployment": "text-embedding-3-large",
    "candidates": [
        {
            "proposedTitle": "Ingest Worker",
            "proposedSlug": "ingest-worker",
            "summary": "How the worker boots and consumes the Service Bus.",
            "highestClassification": "Internal",
            "supportingChunkIds": ["<guid>", "<guid>", "<guid>"],
            "clusterSize": 14
        }
    ]
}
```

Behaviour:
- **Random sample** of chunks for the department (not recency-ordered), so a 10k-chunk corpus gets representative coverage rather than just the newest content.
- **K-means clustering** with k-means++ seeding; k is auto-derived from `maxCandidates` and corpus size so small corpora don't over-split.
- **LLM names each cluster** with `{title, slug, summary}` JSON. Bad slugs are silently replaced with a slug derived from the proposed title.
- **Existing slugs are filtered out** — pages that already exist won't be re-suggested.
- **`highestClassification`** is the highest classification across the cluster's source chunks; treat it as the recommended facet ceiling.
- **`supportingChunkIds`** is the cluster's representatives — useful to surface in a "show me why" review.
- Returns nothing if the corpus is empty (no Postgres / no sources for this department); the dev-without-Postgres `NullChunkSampler` is the same path.

Cost: 1 batched embedding call + up to `maxCandidates` small chat calls per discovery run.

To materialize a candidate, copy `proposedTitle`, `proposedSlug`, and
`highestClassification` into a body for `/api/admin/wiki/discover`
along with a `topic` (typically the proposed title or summary). The
discovery endpoint chains naturally into maintenance.

Tuning lives under `WikiCandidateDiscovery` in `appsettings.json`:

| Knob | When to change |
|------|---------------|
| `WikiCandidateDiscovery:EmbeddingDeployment` | Defaults to the `Search:EmbeddingDeployment`. Override only if you want a different embedding model for clustering than for retrieval. |
| `WikiCandidateDiscovery:ChatDeployment` | Default `gpt-4o-mini`. Raise for higher naming quality at higher cost. |
| `WikiCandidateDiscovery:RepresentativesPerCluster` | Default 3. Number of cluster-centroid chunks sent to the LLM per naming call. |
| `WikiCandidateDiscovery:MaxCharsPerChunk` | Default 2048. Per-chunk cap; smaller than the maintenance cap because naming only needs topical signal. |
| `WikiCandidateDiscovery:NamingTemperature` | Default 0.2. Lower for more stable proposals; higher for variety. |

Audit: emits one `wiki/discover.candidates` Critical row per call,
details include sampled / returned counts and embedding deployment.
Per-candidate decisions are audited later when the operator calls
`/discover` for each one they pick.

## Page lifecycle (rename + lock + soft-delete)

The discover endpoint stays additive (never overwrites title, never
touches `locked`). To rename a page or flip its lock state, use:

```powershell
$token = "<bearer for Admin caller>"

# Rename
$body = @{ title = "Ingest Worker (Phase 2)" } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Patch `
    -Uri "https://<api-host>/api/admin/wiki/pages/<page-guid>" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" -Body $body

# Lock (routes future maintenance into the proposal queue)
$body = @{ locked = $true } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Patch `
    -Uri "https://<api-host>/api/admin/wiki/pages/<page-guid>" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" -Body $body

# Combined: rename and unlock in one call
$body = @{ title = "Renamed"; locked = $false } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Patch -Uri "https://<api-host>/api/admin/wiki/pages/<page-guid>" `
    -Headers @{ Authorization = "Bearer $token" } -ContentType "application/json" -Body $body
```

Response shape:
```json
{ "pageId": "...", "titleUpdated": true, "lockedUpdated": false }
```

Notes:
- **Slug stays frozen.** The canonical identity is `(department_id, slug)`; renaming a page changes only the human-readable label. Old URLs keep working.
- **404** when the page id doesn't exist.
- **400** when neither `title` nor `locked` is supplied.
- Admin-only. Locking gates future revisions through the approval queue; existing pending proposals on that page continue under whatever lock state they were filed under.

### Soft-delete a page

When a page should no longer surface in retrieval or wiki listings,
soft-delete it (ADR 0008 Tier-1 deletion). The row stays in the
table for audit but RLS hides it from every read; downstream
facets / revisions / claims / citations follow transitively.

```powershell
$token = "<bearer for Admin caller>"
Invoke-RestMethod -Method Delete `
    -Uri "https://<api-host>/api/admin/wiki/pages/<page-guid>" `
    -Headers @{ Authorization = "Bearer $token" }
```

Response: `{ "pageId": "...", "softDeleted": true }`.

Notes:
- **Idempotent.** Re-deleting a soft-deleted page returns 404 (the
  writer reports "not transitioned" for both missing AND
  already-deleted, and the handler maps both to 404).
- **Slug becomes free for reuse.** The `wiki_pages` table has a
  partial unique index `ux_wiki_pages_dept_slug_live` (migration
  0027) keyed only on live rows; after soft-delete, a fresh
  `/discover` call with the same slug creates a new row with a new
  id.
- **Cascade behaviour.** `audit_dangling_citations` runs as
  `SECURITY INVOKER` and respects the caller's RLS; once a page is
  soft-deleted, citations on it stop appearing as "dangling" to the
  cascade-regeneration worker. The worker will not attempt to
  regenerate a soft-deleted page's facets.
- **Undo.** See "Restore a soft-deleted page" below.
- **Admin-only.** Same gate as rename/lock.

### Restore a soft-deleted page

To un-delete a page, hit the restore endpoint:

```powershell
$token = "<bearer for Admin caller>"
Invoke-RestMethod -Method Post `
    -Uri "https://<api-host>/api/admin/wiki/pages/<page-guid>/restore" `
    -Headers @{ Authorization = "Bearer $token" }
```

Three response shapes:

- **200** — `{ "pageId": "...", "outcome": "restored" }`. The row is live again.
- **404** — `{ "pageId": "...", "found": false }`. Either the id is unknown OR the row is already live (both surface the same).
- **409** — `{ "pageId": "...", "reason": "slug_already_in_use_by_live_page", "conflictingLivePageId": "..." }`. The slug has been reused by a fresh page since the soft-delete. To proceed, the operator must either rename / re-delete the conflicting page, or accept that the restore won't happen.

The endpoint runs a single transaction: lookup → conflict-check → UPDATE. The 409 path is a no-op write (no audit row); the operator's next action (rename / re-delete the conflict) is the audited event.

## Tuning

| Knob | When to change |
|------|---------------|
| `WikiMaintainer:Model` | Default `gpt-4o-mini`. Bump to a larger model for higher Pass 1 quality at higher cost. |
| `WikiMaintainer:Temperature` | Default 0.2. Lower (0.0-0.1) for more stable citations; higher for more prose variation. |
| `WikiMaintainer:DefaultCitationConfidence` | Default 0.85. Must stay ≥ `Quality:CitationValidator:ConfidenceFloor` (default 0.7) until embedding-similarity scoring lands. |
| `WikiMaintenance:RetrievalLimit` | Default 20. More chunks = better synthesis coverage, but pushes the LLM's context window and increases token cost. |
| `WikiMaintenance:Interval` | Default 1 hour. Lower for fast-moving corpora; raise for low-churn departments. |
| `WikiMaintenance:MaxFacetsPerCascadeTick` | Default 10. Raise after a known mass-delete event; lower to throttle. |
| `WikiMaintenance:MaxChunkContentChars` | Default 4096. Cap on full canonical content fetched per cited chunk after hybrid retrieval. Bounded so one huge chunk (e.g. a multi-page PDF) can't blow the LLM context. Set to 600 to disable the upgrade and feed Pass 1 the raw hybrid excerpts. Raise (up to 65 536) when chunks are large and synthesis is missing context. |

## Approval queue (Phase 2.5)

When `wiki_pages.locked = true`, the Wiki Maintainer does NOT commit
directly. It writes a proposed revision into `wiki_proposed_revisions`
(migration 0030) and returns
`succeeded = false` with `rejectionReason = "Page is locked; proposal
queued as <guid> (expires in 14 days)."`. A Reviewer or Librarian on
the page's department then decides. The 14-day SLA (ADR 0006 Q13)
auto-rejects anything left unreviewed.

### List the queue

```powershell
$token = "<bearer for Reviewer/Librarian/Admin>"
Invoke-RestMethod -Method Get `
    -Uri "https://<api-host>/api/admin/wiki/proposed?state=pending" `
    -Headers @{ Authorization = "Bearer $token" }
```

RLS-filtered: reviewers see only proposals on pages they can decide.
`state` is optional (`pending` / `accepted` / `rejected` / `expired`);
omit for all.

### Accept

```powershell
Invoke-RestMethod -Method Post `
    -Uri "https://<api-host>/api/admin/wiki/proposed/<proposal-id>/accept" `
    -Headers @{ Authorization = "Bearer $token" }
```

The API transitions the proposal to `accepted` AND materializes the
JSONB payload into a fresh revision in **one transaction** — no
half-state where the proposal says accepted but the revision is
missing. Emits `wiki/proposal.accepted` audit.

### Reject

```powershell
$body = @{ reason = "Source X retiring; regenerate after cutover." } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post `
    -Uri "https://<api-host>/api/admin/wiki/proposed/<proposal-id>/reject" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" `
    -Body $body
```

Emits `wiki/proposal.rejected` audit with the operator's reason.

### Bulk reject (after a source-retirement sweep)

When a mass source retirement leaves dozens of stale pending proposals,
reject them all in one call:

```powershell
$token = "<bearer for Admin/Reviewer/Librarian>"
$body = @{
    proposalIds = @("<guid1>","<guid2>","<guid3>")    # up to 200 per call
    reason      = "Source X retired in change request CR-1234."
} | ConvertTo-Json -Compress

Invoke-RestMethod -Method Post `
    -Uri "https://<api-host>/api/admin/wiki/proposed/bulk-reject" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" `
    -Body $body
```

Response shape:
```json
{ "rejected": ["..."], "skipped": ["..."], "notFound": ["..."] }
```

- `rejected` — were pending, now `rejected` with your reason stamped on `decision_reason`.
- `skipped` — existed but already had a terminal state (accepted/rejected/expired); no overwrite.
- `notFound` — id didn't exist in the table.

Emits one `wiki/proposal.bulk_rejected` audit row per call (not per
proposal) with aggregate counts; per-row attribution stays on
`wiki_proposed_revisions.decided_by` + `decision_reason`. Cap is **200
rows per call**; submit in smaller chunks for larger sweeps.

### My recent decisions (audit)

For a librarian audit dashboard or SOC-2-style attestation report:

```powershell
$token = "<bearer for Reviewer/Librarian/Admin>"
# Caller's own decisions, last 30 days (default when decidedBy is omitted).
Invoke-RestMethod -Method Get `
    -Uri "https://<api-host>/api/admin/wiki/proposed/decisions?since=2026-04-14T00:00:00Z" `
    -Headers @{ Authorization = "Bearer $token" }

# Admin can query any decider's history:
Invoke-RestMethod -Method Get `
    -Uri "https://<api-host>/api/admin/wiki/proposed/decisions?decidedBy=<user-guid>&limit=100" `
    -Headers @{ Authorization = "Bearer $token" }
```

- Returns proposals whose `state` is `accepted`/`rejected`/`expired`. Pending proposals never appear here — use `/api/admin/wiki/proposed?state=pending` for the queue view.
- Ordered by `decided_at` descending.
- Non-Admin callers default to their own history; an explicit `decidedBy` other than your own user id is rejected with 403.

### Expiry sweep

The cascade hosted service runs an expiry sweep on every tick:
`UPDATE wiki_proposed_revisions SET state='expired'` on every pending
row past `expires_at`. `decision_reason` becomes `"expired without
review"` and `decided_by` is the system sentinel.

### One pending per facet

The unique index `ux_wiki_proposed_revisions_pending_per_facet` allows
at most one pending proposal per facet at a time. A second maintenance
run on a facet with an existing pending proposal raises a 23505. Decide
or expire the existing proposal first.

## Known limitations

- **Pass 1 now sees full canonical content up to a cap (4096 chars by
  default).** The hybrid-search return shape uses
  `left(content_markdown, 600)` for retrieval speed, but after retrieval
  the source-pool builder upgrades each hit via `IChunkContentReader`
  (Postgres-backed, system-admin RLS context, server-side
  `left(content_markdown, @cap)` truncation). The cap is configurable
  via `WikiMaintenance:MaxChunkContentChars`. Failures (Postgres hiccup,
  RLS-hidden chunk) fall back to the 600-char retrieval excerpt — a
  transient database problem never fails a maintenance pass. Set the
  cap to 600 to opt out of the upgrade entirely.
- **Confidence is a placeholder by default; opt in via config.**
  Every citation gets `WikiMaintainer:DefaultCitationConfidence`
  (default 0.85) until you set
  `WikiMaintainer:EmbeddingScorer:EmbeddingDeployment` to a deployed
  embedding model (e.g. `text-embedding-3-large`). When set, the
  maintainer runs two batched embedding calls per maintenance pass
  (one for distinct claim texts, one for cited-chunk content) and
  replaces each citation's confidence with the
  `cosine(claim_text, chunk_content)` similarity clamped to
  `[0, 1]`. Embedding failures fall back to the placeholder rather
  than rejecting the run, so a transient outage isn't fatal.
- **Persona-aware retrieval is wired.** When a `personaId` is set on
  the session, the hybrid search decorator over-fetches a multiple of
  the requested limit and reranks the candidates by the persona's
  retrieval profile (ADR 0015) before returning the top N. All four
  reranker dimensions are now active: source-type weights (against
  `sources.source_type` from migration 0028 — `SourceTypeClassifier`
  in `AiLibrarian.Domain.Sources` is the pure-function classifier
  the ingestion pipeline calls to stamp the column; hits with NULL
  `source_type` get no weight applied, equivalent to "no opinion"),
  recency-decay, authority bias (current vs draft, derived from
  `sources.approved_at`), and cross-department boost. The reranker
  NEVER widens the authorized set; RLS narrows first, persona only
  reorders. Tunable via `PersonaRetrieval` config
  (`OverFetchFactor`, `MaxOverFetchLimit`). See
  `db/changelog/seed/persona-retrieval-profiles-v1.sql` for the
  Engineering pilot profile.
- **Persona-aware synthesis style is wired.** When `personaId` is set
  on a maintenance request, the Wiki Maintainer loads the persona's
  `synthesis_style` JSONB and appends a "Style hints" block to the
  Pass-1 system prompt. Style fields supported in v1:
  `answerLengthHint`, `structurePreference`, `citationDensity`,
  `codeQuoting`, `hedgingPosture`, `crossSourceSynthesis`,
  `showSourceMetadata`. The structural rules (cite every claim, no
  fabrication, treat sources as data) are NOT configurable through
  the style — the validator enforces them regardless. A missing /
  malformed / deactivated profile degrades to neutral with a warn.
  `abstentionThreshold` is captured but not yet applied by the
  Wiki Maintainer (future ask/synthesize tools will consult it).
  See `db/changelog/seed/persona-synthesis-styles-v1.sql` for the
  Engineering pilot style.

## Verification

A successful end-to-end smoke test after deployment:

1. Create a test page + facet in the Engineering department.
2. POST `/api/admin/wiki/maintain` with a topic that matches several
   approved Engineering sources.
3. Confirm the response has `succeeded=true` and a non-null
   `revisionId`.
4. Query `wiki_page_revisions WHERE id = <revisionId>` — the row
   exists with the claim count from the response.
5. Query `wiki_claim_citations WHERE claim_id IN (...)` — citation
   rows exist.
6. Query `page_facets WHERE page_id = ... AND min_classification = ...`
   — `current_revision_id` matches the returned revision.

If you want to exercise the cascade worker:

1. Flip `WikiMaintenance:CascadeRegenerationEnabled = true`, restart
   the API.
2. Soft-delete one of the sources cited in the revision you just
   committed (`UPDATE sources SET soft_deleted_at = now() WHERE id = ...`
   from a Postgres admin shell).
3. Wait for the next interval, or just look at the audit ledger:
   `SELECT * FROM audit_events WHERE event_type = 'wiki' AND event_subtype IN ('regen.facet','regen.tick') ORDER BY occurred_at DESC LIMIT 5;`
4. The facet should regenerate with a fresh source pool, and the
   `current_revision_id` should bump.
