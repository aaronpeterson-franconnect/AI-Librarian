<#
.SYNOPSIS
    Sweeps sources rows that never got chunked and re-enqueues them
    through POST /api/ingest/enqueue.

.DESCRIPTION
    Operational tool for the pilot. Some early uploads (before the
    Portal auto-enqueue chain landed) created a sources row but
    never produced a Service Bus message, so the worker never saw
    them. The Source-detail page in the portal shows them as
    "(not yet computed by the worker)" indefinitely.

    This script finds those rows (sources WITH NO source_chunks)
    and POSTs each to /api/ingest/enqueue with the original blob
    URI and content type. The API audits each enqueue per usual
    so the operation leaves a trace.

    Idempotent — running twice doesn't double-process; the worker's
    chunk-persistence path is upsert-shaped via the (source_id,
    order_index) unique constraint on source_chunks.

.PARAMETER ApiBaseUrl
    Base URL of the running API. Defaults to http://localhost:5071
    (the pilot's local API listener).

.PARAMETER PostgresConnectionString
    Npgsql connection string for the catalog database. Defaults to
    $env:ConnectionStrings__Postgres so a loaded pilot.local.env
    Just Works.

.PARAMETER DryRun
    When set, prints the rows that WOULD be enqueued but does not
    actually POST. Use this first.

.PARAMETER OlderThanMinutes
    Only consider sources older than this many minutes. Default 5;
    prevents racing the worker on freshly uploaded items.

.PARAMETER MaxRows
    Safety cap. Default 100. The query returns at most this many
    rows even if more match; re-run to drain a deeper backlog.

.EXAMPLE
    # See what's stuck before doing anything
    .\deploy\scripts\Backfill-StaleSources.ps1 -DryRun

.EXAMPLE
    # Drain everything older than 5 minutes
    .\deploy\scripts\Backfill-StaleSources.ps1

.EXAMPLE
    # Different API listener
    .\deploy\scripts\Backfill-StaleSources.ps1 -ApiBaseUrl https://ca-api-xxxx.azurecontainerapps.io
#>

[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://localhost:5071',
    [string]$PostgresConnectionString = $env:ConnectionStrings__Postgres,
    [switch]$DryRun,
    [int]$OlderThanMinutes = 5,
    [int]$MaxRows = 100
)

$ErrorActionPreference = 'Stop'

if (-not $PostgresConnectionString) {
    throw "PostgresConnectionString is required. Either pass -PostgresConnectionString or load deploy/pilot.local.env so ConnectionStrings__Postgres is set."
}

# Parse the Npgsql connection string into a libpq DSN for docker psql.
$parts = @{}
$PostgresConnectionString.Split(';') | ForEach-Object {
    $kv = $_.Split('=', 2)
    if ($kv.Length -eq 2 -and $kv[0]) {
        $parts[$kv[0].Trim().ToLower()] = $kv[1]
    }
}
foreach ($k in 'host', 'port', 'database', 'username', 'password') {
    if (-not $parts.ContainsKey($k)) {
        throw "ConnectionStrings__Postgres missing '$k'. Got: $($parts.Keys -join ', ')"
    }
}

$dsn = "host=$($parts['host']) port=$($parts['port']) dbname=$($parts['database']) user=$($parts['username']) password=$($parts['password']) sslmode=require"

Write-Host ""
Write-Host "==> Backfill stale sources" -ForegroundColor Cyan
Write-Host "    api          : $ApiBaseUrl"
Write-Host "    pg host      : $($parts['host'])"
Write-Host "    older than   : ${OlderThanMinutes}m"
Write-Host "    max rows     : $MaxRows"
Write-Host "    dry-run      : $($DryRun.IsPresent)"
Write-Host ""

# Find sources with no chunks that aren't soft-deleted, sorted oldest-first.
# Outputs as tab-separated lines so PowerShell can parse without docker
# pulling in a JSON library.
$query = @"
SELECT
    s.id::text,
    s.uri,
    COALESCE(s.content_type, ''),
    COALESCE(s.title, '')
FROM sources s
LEFT JOIN source_chunks sc ON sc.source_id = s.id
WHERE s.soft_deleted_at IS NULL
  AND sc.id IS NULL
  AND s.uri IS NOT NULL
  AND s.uri <> ''
  AND s.created_at < now() - interval '${OlderThanMinutes} minutes'
GROUP BY s.id, s.uri, s.content_type, s.title
ORDER BY s.created_at ASC
LIMIT ${MaxRows};
"@

Write-Host "==> Querying catalog for stale sources..." -ForegroundColor Yellow
$rawRows = & docker run --rm -i postgres:16 psql "$dsn" -At -F "`t" -c "$query" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "psql failed:`n$rawRows"
}

$rows = @()
foreach ($line in $rawRows) {
    if (-not $line) { continue }
    $cells = $line -split "`t"
    if ($cells.Count -lt 4) { continue }
    $rows += [pscustomobject]@{
        SourceId    = $cells[0]
        BlobUri     = $cells[1]
        ContentType = $cells[2]
        Title       = $cells[3]
    }
}

if ($rows.Count -eq 0) {
    Write-Host "    No stale sources found. Everything that exists has chunks." -ForegroundColor Green
    exit 0
}

Write-Host "    Found $($rows.Count) stale source(s):"
$rows | Format-Table SourceId, Title, ContentType -AutoSize

if ($DryRun.IsPresent) {
    Write-Host ""
    Write-Host "==> Dry-run complete. Re-run without -DryRun to enqueue." -ForegroundColor Cyan
    exit 0
}

Write-Host "==> Enqueuing each through ${ApiBaseUrl}/api/ingest/enqueue..." -ForegroundColor Yellow

$succeeded = 0
$failed = 0
foreach ($row in $rows) {
    $body = @{
        blobUri          = $row.BlobUri
        sourceId         = $row.SourceId
        contentType      = if ($row.ContentType) { $row.ContentType } else { $null }
        originalFileName = if ($row.Title) { $row.Title } else { $null }
    } | ConvertTo-Json -Compress

    try {
        $resp = Invoke-RestMethod `
            -Method Post `
            -Uri "$ApiBaseUrl/api/ingest/enqueue" `
            -ContentType 'application/json' `
            -Body $body `
            -ErrorAction Stop
        $succeeded++
        Write-Host "    ok   $($row.SourceId)  msg=$($resp.messageId)" -ForegroundColor DarkGray
    } catch {
        $failed++
        Write-Host "    fail $($row.SourceId)  $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "==> Summary" -ForegroundColor Cyan
Write-Host "    queued    : $succeeded"
Write-Host "    failed    : $failed"
Write-Host "    total     : $($rows.Count)"

if ($failed -gt 0) {
    Write-Host "    (Non-zero failure count -- check audit_events for ingest/enqueue.failed entries.)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "==> Done. The worker will pick the queued messages up next; refresh the Sources page to see chunks land." -ForegroundColor Green
