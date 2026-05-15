<#
.SYNOPSIS
    Exports shadow-mode redaction candidates from audit_events into a
    CSV the operator labels by hand, then feeds back through
    `ailib precision-sampling` (ADR 0017 enforce-mode gate).

.DESCRIPTION
    Pulls every `tool/ask.*` audit row whose details.redaction_candidates
    is > 0, emits one CSV row per row (NOT one per individual candidate
    — that level of detail isn't in the audit row by design; per-call
    counts are enough to size the sampling effort, and the operator
    pulls the actual ask answers separately to label).

    Workflow:
    1. Run this script to produce candidates.csv.
    2. Spot-check a representative subset of the ask invocations
       (use the row's correlation_id to find the full answer in
       application logs / source traces).
    3. Append `kind,is_true_positive` columns for each labeled match.
    4. Feed labeled-candidates.csv through:
           ailib precision-sampling --labels labeled-candidates.csv
    5. If verdict is ENFORCE-READY, flip Mcp:AskGuard:RedactionMode = Enforce.

    The actual candidate-level detail (kind, offset, length) lives in
    AskGuardResult but is NOT persisted to the audit row -- ADR 0017
    intentionally captures only the count to avoid storing partial
    secret content. Labeling therefore requires the operator to access
    application-layer logs or replay the ask call to see what matched.

.PARAMETER PostgresConnectionString
    Npgsql connection string. Defaults to $env:ConnectionStrings__Postgres.

.PARAMETER OutputPath
    CSV destination. Default: ./candidates.csv

.PARAMETER SinceHours
    Look-back window. Default 168 (one week).

.PARAMETER MinCandidates
    Only export rows with redaction_candidates >= this. Default 1.

.EXAMPLE
    .\deploy\scripts\Export-RedactionCandidates.ps1

.EXAMPLE
    # Two-week window, only multi-candidate calls
    .\deploy\scripts\Export-RedactionCandidates.ps1 -SinceHours 336 -MinCandidates 2
#>

[CmdletBinding()]
param(
    [string]$PostgresConnectionString = $env:ConnectionStrings__Postgres,
    [string]$OutputPath = "./candidates.csv",
    [int]$SinceHours = 168,
    [int]$MinCandidates = 1
)

$ErrorActionPreference = 'Stop'

if (-not $PostgresConnectionString) {
    throw "PostgresConnectionString is required. Pass it or load deploy/pilot.local.env."
}

$parts = @{}
$PostgresConnectionString.Split(';') | ForEach-Object {
    $kv = $_.Split('=', 2)
    if ($kv.Length -eq 2 -and $kv[0]) {
        $parts[$kv[0].Trim().ToLower()] = $kv[1]
    }
}
foreach ($k in 'host', 'port', 'database', 'username', 'password') {
    if (-not $parts.ContainsKey($k)) {
        throw "ConnectionStrings__Postgres missing '$k'."
    }
}
$dsn = "host=$($parts['host']) port=$($parts['port']) dbname=$($parts['database']) user=$($parts['username']) password=$($parts['password']) sslmode=require"

$sql = @"
SELECT
    id::text,
    occurred_at::text,
    actor_user_id::text,
    event_subtype,
    COALESCE((details->>'redaction_candidates')::int, 0)            AS candidates,
    COALESCE(details->>'redaction_mode', '')                        AS mode,
    COALESCE(details->>'query_sha256', '')                          AS query_sha256,
    COALESCE((details->>'chunk_count')::int, 0)                     AS chunk_count
FROM audit_events
WHERE event_type = 'tool'
  AND event_subtype LIKE 'ask.%'
  AND occurred_at > now() - interval '$SinceHours hours'
  AND COALESCE((details->>'redaction_candidates')::int, 0) >= $MinCandidates
ORDER BY occurred_at DESC;
"@

Write-Host ""
Write-Host "==> Export redaction candidates" -ForegroundColor Cyan
Write-Host "    output       : $OutputPath"
Write-Host "    since hours  : $SinceHours"
Write-Host "    min candid.  : $MinCandidates"
Write-Host "    pg host      : $($parts['host'])"
Write-Host ""

$raw = & docker run --rm -i postgres:16 psql "$dsn" -At -F "`t" -c "$sql" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "psql failed:`n$raw"
}

$header = "audit_id,occurred_at,actor_user_id,event_subtype,redaction_candidates,redaction_mode,query_sha256,chunk_count,kind,is_true_positive"
$lines = @($header)
$rowCount = 0
foreach ($line in $raw) {
    if (-not $line) { continue }
    $cells = $line -split "`t"
    if ($cells.Count -lt 8) { continue }
    # Append empty kind / is_true_positive for the operator to fill in.
    $lines += ($cells -join ',') + ',,'
    $rowCount++
}

[System.IO.File]::WriteAllLines($OutputPath, $lines, [System.Text.UTF8Encoding]::new($false))

Write-Host "==> Wrote $rowCount row(s) to $OutputPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Open $OutputPath in a spreadsheet."
Write-Host "  2. For each row, look up the ask call by audit_id / correlation in your"
Write-Host "     application logs to see the LLM output that contained the candidate(s)."
Write-Host "  3. Fill in 'kind' (e.g. 'jwt', 'aws_access_key') and 'is_true_positive'"
Write-Host "     (true / false) per row. If a single ask call had multiple kinds,"
Write-Host "     duplicate the row and label each kind separately."
Write-Host "  4. Save the labeled CSV (only 'kind' + 'is_true_positive' columns are read)."
Write-Host "  5. Run: ailib precision-sampling --labels <labeled-csv>"
Write-Host "  6. If ENFORCE-READY, edit Mcp:AskGuard:RedactionMode = 'Enforce' and restart the API."
