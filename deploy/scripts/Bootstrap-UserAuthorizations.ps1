<#
.SYNOPSIS
    Grants a role to a user in user_authorizations. Pre-Entra-group-sync
    operational tool for the pilot.

.DESCRIPTION
    Until the Entra group-sync job exists, role grants are operator-
    managed. This script lets an operator with the Postgres admin
    connection string assign one of the five roles
    (Reader / Contributor / Reviewer / Librarian / Admin) to a
    specific user OID for a specific department (or system-wide for
    Admin).

    The OID is the same value Entra issues as the user's `oid` claim
    AND the same value used as `users.id` in the database. The user
    row itself does NOT need to exist yet — JIT provisioning creates
    it the first time that user signs in. Authorizations granted
    before the user's first sign-in are still valid; the API joins
    them in once the row materializes.

    Idempotent — re-running the same grant is a no-op via the
    (user_id, department_id, role) unique index.

.PARAMETER UserOid
    Entra OID. Must be a valid GUID.

.PARAMETER Role
    One of: Reader, Contributor, Reviewer, Librarian, Admin.
    Admin grants are system-wide (DepartmentId must be omitted).

.PARAMETER DepartmentId
    Department GUID. Required for non-Admin roles. Must be omitted
    for Admin (the schema's chk_user_auth_admin_no_dept constraint
    enforces this; the script also validates upfront for a clearer
    error than a constraint violation).

.PARAMETER SourceGroupLabel
    Audit-anchor label stored in user_authorizations.source_group_id.
    Pre-Entra-group-sync, use a stable label like
    "bootstrap-eng-librarian" so a future group-sync job can detect
    and reconcile manually-granted rows. Defaults to "bootstrap".

.PARAMETER PostgresConnectionString
    Admin-or-equivalent connection string. The Postgres admin role
    has BYPASSRLS implicit, which is required to write to
    user_authorizations (p_user_auth_write is Admin-only via the
    in-database app_is_admin() helper). Defaults to
    $env:ConnectionStrings__Postgres so a loaded pilot.local.env
    works without arguments.

.PARAMETER DryRun
    Prints the SQL that would run but does not execute.

.EXAMPLE
    # Pilot Engineer becomes a Librarian for the Engineering department
    .\deploy\scripts\Bootstrap-UserAuthorizations.ps1 `
        -UserOid 22222222-2222-2222-2222-222222222222 `
        -Role Librarian `
        -DepartmentId 11111111-1111-1111-1111-111111111111

.EXAMPLE
    # Grant system-wide Admin
    .\deploy\scripts\Bootstrap-UserAuthorizations.ps1 `
        -UserOid 33333333-3333-3333-3333-333333333333 `
        -Role Admin `
        -SourceGroupLabel "bootstrap-admin"

.EXAMPLE
    # See what would happen first
    .\deploy\scripts\Bootstrap-UserAuthorizations.ps1 `
        -UserOid 22222222-2222-2222-2222-222222222222 `
        -Role Contributor `
        -DepartmentId 11111111-1111-1111-1111-111111111111 `
        -DryRun
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UserOid,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Reader', 'Contributor', 'Reviewer', 'Librarian', 'Admin')]
    [string]$Role,

    [string]$DepartmentId = '',

    [string]$SourceGroupLabel = 'bootstrap',

    [string]$PostgresConnectionString = $env:ConnectionStrings__Postgres,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Validate GUIDs upfront so a typo doesn't surface as a Postgres
# parse error 100 lines down.
$parsedOid = [Guid]::Empty
if (-not [Guid]::TryParse($UserOid, [ref]$parsedOid)) {
    throw "UserOid '$UserOid' is not a valid GUID."
}

$parsedDept = [Guid]::Empty
if ($Role -eq 'Admin') {
    if ($DepartmentId) {
        throw "Admin role is system-wide. Omit -DepartmentId for Admin grants."
    }
} else {
    if (-not $DepartmentId) {
        throw "Role $Role requires -DepartmentId."
    }
    if (-not [Guid]::TryParse($DepartmentId, [ref]$parsedDept)) {
        throw "DepartmentId '$DepartmentId' is not a valid GUID."
    }
}

if (-not $PostgresConnectionString) {
    throw "PostgresConnectionString is required. Pass -PostgresConnectionString or load deploy/pilot.local.env."
}

# Parse Npgsql connection string into a libpq DSN for the docker psql
# container. Same shape as Backfill-StaleSources.ps1.
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

# Build the SQL. Use ON CONFLICT to make the call idempotent against
# both unique indices (Admin: (user_id, role) WHERE dept IS NULL;
# non-Admin: (user_id, dept, role) WHERE dept IS NOT NULL).
$deptSql = if ($Role -eq 'Admin') { 'NULL' } else { "'$DepartmentId'" }
$conflictClause = if ($Role -eq 'Admin') {
    'ON CONFLICT (user_id, role) WHERE department_id IS NULL DO UPDATE SET source_group_id = EXCLUDED.source_group_id'
} else {
    'ON CONFLICT (user_id, department_id, role) WHERE department_id IS NOT NULL DO UPDATE SET source_group_id = EXCLUDED.source_group_id'
}

$sql = @"
INSERT INTO user_authorizations (user_id, department_id, role, source_group_id)
VALUES ('$UserOid', $deptSql, '$Role', '$SourceGroupLabel')
$conflictClause
RETURNING id, user_id, COALESCE(department_id::text, '<system-wide>'), role, source_group_id;
"@

Write-Host ""
Write-Host "==> Bootstrap user authorization" -ForegroundColor Cyan
Write-Host "    user oid  : $UserOid"
Write-Host "    role      : $Role"
Write-Host "    department: $(if ($Role -eq 'Admin') { '<system-wide>' } else { $DepartmentId })"
Write-Host "    label     : $SourceGroupLabel"
Write-Host "    pg host   : $($parts['host'])"
Write-Host ""

if ($DryRun.IsPresent) {
    Write-Host "==> Dry-run -- SQL that would execute:" -ForegroundColor Yellow
    Write-Host $sql -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "==> Re-run without -DryRun to apply." -ForegroundColor Cyan
    exit 0
}

Write-Host "==> Applying grant..." -ForegroundColor Yellow
$output = & docker run --rm -i postgres:16 psql "$dsn" -At -F "`t" -c "$sql" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "psql failed:`n$output"
}

if (-not $output) {
    Write-Host "    No row returned -- the unique-index conflict path didn't update (idempotent no-op)." -ForegroundColor DarkGray
} else {
    $output | ForEach-Object { Write-Host "    $_" }
}

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    The grant is live immediately. Next sign-in by this user picks up the role"
Write-Host "    via SessionContextResolver -> PostgresUserDirectory.GetProjectionAsync."
