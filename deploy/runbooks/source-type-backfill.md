# Source-type backfill — operational guide

> Closes the ADR 0015 `sourceTypeWeights` thread. New ingests stamp
> `sources.source_type` at INSERT time (via
> `SourceTypeClassifier`); rows created before that wiring stay
> NULL until this maintenance procedure runs over them.

## Why this exists

`sources.source_type` was added in migration `0028`. Rows inserted
before that migration ran — and any rows inserted through a code path
that pre-dates the classifier wiring — have `source_type IS NULL`.
The persona reranker treats NULL as "no opinion" (no
`sourceTypeWeights` multiplier applied), so unclassified rows
silently miss the persona-aware boost / penalty their type would
otherwise earn.

The backfill endpoint sweeps NULL rows in bounded batches, applies
the same `SourceTypeClassifier` the live INSERT path uses, and
returns a progress snapshot. Idempotent: re-running picks up where
the prior call stopped because the WHERE clause filters out rows
that are already classified.

## How to drive it

```powershell
$token = "<bearer for Admin caller>"

# One batch (default 100 rows). Repeat until remainingUnclassified == 0.
Invoke-RestMethod -Method Post `
    -Uri "https://<api-host>/api/admin/sources/source-type/backfill" `
    -Headers @{ Authorization = "Bearer $token" }

# Explicit batch size, capped at 500 server-side.
Invoke-RestMethod -Method Post `
    -Uri "https://<api-host>/api/admin/sources/source-type/backfill?batchSize=250" `
    -Headers @{ Authorization = "Bearer $token" }
```

Response shape:

```json
{
    "classifiedThisCall": 100,
    "remainingUnclassified": 4321,
    "classificationCounts": {
        "document": 67,
        "runbook": 18,
        "code": 9,
        "ticket": 4,
        "email": 2
    }
}
```

A simple loop until done (bash):

```bash
while true; do
  REMAINING=$(curl -fsS -X POST \
    -H "Authorization: Bearer $TOKEN" \
    "https://$API_HOST/api/admin/sources/source-type/backfill?batchSize=500" \
    | jq -r '.remainingUnclassified')
  echo "remaining=$REMAINING"
  if [ "$REMAINING" -eq 0 ]; then break; fi
done
```

## Audit

Every call emits one `admin/source_type.backfill` audit row at
`AuditCriticality.Critical`. Details carry:
- `batch_size` — the requested batch size (clamped to [1, 500])
- `classified` — how many rows this call transitioned
- `remaining_unclassified` — how many NULL rows remain after this call
- `counts` — per-source-type classification distribution for the batch

Use the `counts` distribution as a sanity check. If everything is
landing in `document` and you expected a mix, the classifier's
keyword cascade isn't catching your titles — adjust the keywords in
`src/AiLibrarian.Domain/Sources/SourceType.cs:SourceTypeClassifier`
and re-run the backfill (rows already classified as `document`
won't be re-classified; that's a known limitation of the v1
backfill — see "Known limits" below).

## Concurrency

The Postgres backfiller uses `FOR UPDATE SKIP LOCKED`. Two operators
running the endpoint in parallel naturally partition the work — each
call sees only rows the other hasn't locked. Safe to script alongside
manual operator runs.

## Known limits

- **Filename-only signals don't fire from the backfill path.** The
  classifier wants `(contentType, fileName, title)`, but
  `sources` has no `filename` column. The backfiller passes
  `fileName: null`, so file-extension cascade rules (`.sql`, `.py`,
  etc.) don't fire. Operators that need filename-aware
  classification embed the filename in the title at upload — both
  the live INSERT and the backfill then see it.
- **Re-classification is not supported.** A row that was previously
  classified (correctly OR incorrectly) is not touched by
  subsequent backfill calls. To fix a misclassified row, an
  operator with direct DB access sets `source_type = NULL` and
  re-runs the endpoint. A future slice could add an explicit
  re-classify endpoint with a reason audit.
- **No streaming.** Each call returns when its batch is complete.
  The endpoint is bounded (500-row cap) so long-running HTTP isn't
  a concern, but very large corpora require many calls. The loop
  pattern above is the recommended driver.

## Verification

Spot-check after a backfill run:

```sql
-- Distribution across the taxonomy.
SELECT source_type, COUNT(*) AS rows
FROM sources
WHERE source_type IS NOT NULL
GROUP BY source_type
ORDER BY rows DESC;

-- Any rows still missing classification.
SELECT COUNT(*) FROM sources WHERE source_type IS NULL;

-- Sample a few rows the classifier landed in 'document' to confirm
-- they're genuinely uncategorisable (not e.g. mistitled runbooks).
SELECT id, title, content_type
FROM sources
WHERE source_type = 'document'
ORDER BY random()
LIMIT 20;
```
