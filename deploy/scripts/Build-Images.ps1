<#
.SYNOPSIS
    Builds, tags, and optionally pushes the AI Librarian container images.

.DESCRIPTION
    Three workloads ship as containers: API, IngestWorker, Portal. This
    script builds all three from repository root (so the docker build
    context includes Directory.Packages.props + the entire src/ tree),
    tags them against a configurable registry, and optionally pushes.

    Image names are stable: ailib-api, ailib-ingest, ailib-portal.

.PARAMETER Registry
    Container registry hostname WITHOUT trailing slash. Examples:
        myacr.azurecr.io
        ghcr.io/my-org
        ailibrarian
    When omitted, images are built locally with no registry prefix
    (useful for `docker run` smoke tests).

.PARAMETER Tag
    Image tag. Defaults to the short git SHA when available, else 'local'.

.PARAMETER Push
    When set, runs `docker push` for each tagged image. Requires the
    caller to have logged in to the registry already (`az acr login`,
    `docker login ghcr.io`, etc.).

.PARAMETER Workloads
    Comma-separated list of workloads to build. Default: all three.
    Examples:
        -Workloads api
        -Workloads api,worker
        -Workloads portal

.EXAMPLE
    # Build all three locally for a smoke test
    .\deploy\scripts\Build-Images.ps1

.EXAMPLE
    # Build + push to Azure Container Registry
    az acr login --name myacr
    .\deploy\scripts\Build-Images.ps1 -Registry myacr.azurecr.io -Tag v1.0.0 -Push

.EXAMPLE
    # Rebuild just the worker against ACR after a Skills.Pdf change
    .\deploy\scripts\Build-Images.ps1 -Registry myacr.azurecr.io -Workloads worker -Push

.NOTES
    Run from repository root or any subdirectory; the script resolves
    the repo root via $PSScriptRoot.
#>

[CmdletBinding()]
param(
    [string]$Registry = '',
    [string]$Tag = '',
    [switch]$Push,
    [ValidateNotNullOrEmpty()]
    [string]$Workloads = 'api,worker,portal'
)

$ErrorActionPreference = 'Stop'

# Repo root is two levels up from this script (deploy/scripts/).
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repoRoot

# Resolve tag: explicit -Tag wins, else git short SHA, else 'local'.
if (-not $Tag) {
    try {
        $sha = (& git rev-parse --short HEAD 2>$null).Trim()
        if ($LASTEXITCODE -eq 0 -and $sha) {
            $Tag = $sha
        } else {
            $Tag = 'local'
        }
    } catch {
        $Tag = 'local'
    }
}

# Map workload short-names to (image-name, dockerfile-path).
$workloadMap = @{
    'api'    = @{ Image = 'ailib-api';    Dockerfile = 'src/AiLibrarian.Api/Dockerfile' }
    'worker' = @{ Image = 'ailib-ingest'; Dockerfile = 'src/AiLibrarian.IngestWorker/Dockerfile' }
    'portal' = @{ Image = 'ailib-portal'; Dockerfile = 'src/AiLibrarian.Portal/Dockerfile' }
}

$requested = $Workloads.Split(',') | ForEach-Object { $_.Trim().ToLower() } | Where-Object { $_ }
foreach ($w in $requested) {
    if (-not $workloadMap.ContainsKey($w)) {
        throw "Unknown workload '$w'. Valid values: api, worker, portal."
    }
}

# Sanity: docker reachable?
$null = & docker version --format '{{.Server.Version}}' 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "docker daemon not reachable. Start Docker Desktop, then re-run."
}

Write-Host ""
Write-Host "==> AI Librarian image build" -ForegroundColor Cyan
Write-Host "    repo root : $repoRoot"
Write-Host "    registry  : $(if ($Registry) { $Registry } else { '<none, local only>' })"
Write-Host "    tag       : $Tag"
Write-Host "    workloads : $($requested -join ', ')"
Write-Host "    push      : $($Push.IsPresent)"
Write-Host ""

$built = @()

foreach ($w in $requested) {
    $info = $workloadMap[$w]
    $image = $info.Image
    $dockerfile = $info.Dockerfile

    $localTag = "${image}:${Tag}"
    $fullTag = if ($Registry) { "${Registry}/${image}:${Tag}" } else { $localTag }

    Write-Host "==> Building $image" -ForegroundColor Yellow
    & docker build -f $dockerfile -t $localTag .
    if ($LASTEXITCODE -ne 0) {
        throw "docker build failed for $image"
    }

    if ($Registry) {
        & docker tag $localTag $fullTag
        if ($LASTEXITCODE -ne 0) {
            throw "docker tag failed for $image"
        }
    }

    $built += [pscustomobject]@{
        Workload   = $w
        LocalTag   = $localTag
        FullTag    = $fullTag
        Pushed     = $false
    }

    Write-Host "    built: $fullTag"
    Write-Host ""
}

if ($Push.IsPresent) {
    if (-not $Registry) {
        throw "-Push requires -Registry."
    }

    foreach ($entry in $built) {
        Write-Host "==> Pushing $($entry.FullTag)" -ForegroundColor Yellow
        & docker push $entry.FullTag
        if ($LASTEXITCODE -ne 0) {
            throw "docker push failed for $($entry.FullTag). Confirm you ran 'az acr login --name <acr>' (or 'docker login') first."
        }
        $entry.Pushed = $true
        Write-Host ""
    }
}

Write-Host "==> Summary" -ForegroundColor Cyan
$built | Format-Table Workload, FullTag, Pushed -AutoSize

if (-not $Push.IsPresent -and $Registry) {
    Write-Host "Tip: re-run with -Push to publish, or:" -ForegroundColor DarkGray
    foreach ($entry in $built) {
        Write-Host "    docker push $($entry.FullTag)" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
