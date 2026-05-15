#!/usr/bin/env pwsh
<#
.SYNOPSIS
	One-shot slim-pilot deployment for AI Librarian on Azure.

.DESCRIPTION
	Automates Path A from deploy/runbooks/pilot-minimal-azure.md:
	  1. Create the pilot resource group (idempotent).
	  2. Deploy the slim data plane (Postgres Flexible + Blob + Service Bus Basic)
	     via deploy/bicep/main-pilot.bicep.
	  3. Create the application database (CREATE DATABASE ailibrarian).
	  4. Run every Liquibase changeset under db/changelog/ -- including the
	     0100 RLS recursion fix, which production deployments require.
	  5. Verify 0100 actually landed via pg_policies.
	  6. Seed the Engineering pilot department + a test contributor.
	  7. Pull Storage + Service Bus connection strings.
	  8. Write deploy/pilot.local.env (gitignored) with everything the
	     local apps need.

	Idempotent: re-running against the same resource group converges to
	the same state. Liquibase tracks its own DATABASECHANGELOG; seed
	SQL uses ON CONFLICT DO NOTHING; az deployment names re-update.

	After this script:
	  - Load deploy/pilot.local.env into your shell.
	  - Run the API / Portal / IngestWorker in three terminals.
	  - Smoke test via the Portal at http://localhost:5215.

.PARAMETER ResourceGroup
	Pilot resource group name. Created if missing. Default rg-ai-librarian-pilot.

.PARAMETER Location
	Azure region. Default eastus2 -- matches deploy/bicep/parameters/pilot.bicepparam.

.PARAMETER DeploymentName
	az deployment name (the "deployment" Azure tracks). Use a fresh suffix
	to keep deploy history searchable; reuse to update in place.

.PARAMETER DatabaseName
	Application database created on the Flexible Server. Default 'ailibrarian'.

.PARAMETER Password
	PostgreSQL admin password. If omitted, the script prompts (SecureString).
	Stored in $Env:AILIB_PG_ADMIN_PASSWORD for child processes (Docker /
	psql containers) and cleared at script end.

.PARAMETER SkipMigrations
	Skip the Liquibase step (useful when re-running just to refresh env file).

.PARAMETER WhatIfOnly
	Run `az deployment group what-if` and exit. Nothing else runs.

.EXAMPLE
	pwsh deploy/scripts/Deploy-Pilot.ps1
	# Prompts for password, deploys to rg-ai-librarian-pilot in eastus2.

.EXAMPLE
	pwsh deploy/scripts/Deploy-Pilot.ps1 -WhatIfOnly
	# Just shows the Bicep diff without changing Azure or local files.

.EXAMPLE
	$pwd = ConvertTo-SecureString "S0me-Strong-Pwd!" -AsPlainText -Force
	pwsh deploy/scripts/Deploy-Pilot.ps1 -Password $pwd -DeploymentName 'pilot-002'
#>

[CmdletBinding()]
param(
	[string]$ResourceGroup = 'rg-ai-librarian-pilot',
	[string]$Location = 'eastus2',
	[string]$DeploymentName = 'ai-librarian-pilot-001',
	[string]$DatabaseName = 'ailibrarian',
	[SecureString]$Password,
	[switch]$SkipMigrations,
	[switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 7) {
	$PSNativeCommandUseErrorActionPreference = $true
}

# ---------------------------------------------------------------------------
# Resolve the repo root from the script location (deploy/scripts/Deploy-Pilot.ps1).
# Everything below is relative to that. Nested two-arg Join-Path calls so this
# stays compatible with Windows PowerShell 5.1 (which lacks the three-arg
# Join-Path overload that landed in PowerShell 7).
# ---------------------------------------------------------------------------
$repoRoot = Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')
$bicepDir = Join-Path (Join-Path $repoRoot 'deploy') 'bicep'
$changelogDir = Join-Path (Join-Path $repoRoot 'db') 'changelog'
$envTemplate = Join-Path (Join-Path $repoRoot 'deploy') 'pilot.local.env.template'
$envOut = Join-Path (Join-Path $repoRoot 'deploy') 'pilot.local.env'

function Write-Step {
	param([Parameter(Mandatory)][string]$Message)
	Write-Host ''
	Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-Tool {
	param([Parameter(Mandatory)][string]$Name, [string]$VersionCommand)
	$cmd = Get-Command $Name -ErrorAction SilentlyContinue
	if (-not $cmd) {
		throw "Required tool '$Name' is not on PATH. Install it and re-run."
	}

	if ($VersionCommand) {
		& $Name $VersionCommand.Split(' ') | Out-Null
	}
}

function Assert-DockerRunning {
	# `docker` on PATH means the CLI is installed; it does NOT mean the
	# daemon is reachable. Docker Desktop installed but not started is
	# the most common foot-gun. Probe with `docker info` and fail fast --
	# every later docker-run failure would otherwise be silently swallowed
	# by the `Out-Null` pipes and leave the database empty.
	$null = docker info --format '{{.ServerVersion}}' 2>&1
	if ($LASTEXITCODE -ne 0) {
		throw 'Docker daemon is not reachable. Start Docker Desktop (or your daemon of choice) and re-run this script. Everything past the Azure deploy is idempotent -- partial completions resume cleanly.'
	}
}

function Invoke-DockerOrFail {
	# Wrapper that runs `docker <args>` and throws on non-zero exit.
	# Replaces the `docker run ... | Out-Null` pattern so failures
	# surface immediately with the actual stderr instead of breaking
	# later steps with confusing nulls.
	#
	# Windows PowerShell 5.1 gotcha: with $ErrorActionPreference='Stop',
	# any native-command stderr captured via 2>&1 (e.g. docker's benign
	# "Unable to find image ... locally" before an image pull) becomes a
	# NativeCommandError record that terminates the script BEFORE the
	# process even exits. Drop EAP to 'Continue' for the capture window;
	# $LASTEXITCODE remains the authoritative success signal.
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$Operation,
		[Parameter(Mandatory)][string[]]$DockerArgs,
		[string]$Stdin
	)

	$prevEap = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'
	try {
		if ($PSBoundParameters.ContainsKey('Stdin')) {
			$output = $Stdin | & docker @DockerArgs 2>&1
		}
		else {
			$output = & docker @DockerArgs 2>&1
		}
	}
	finally {
		$ErrorActionPreference = $prevEap
	}

	if ($LASTEXITCODE -ne 0) {
		$message = if ($output) { ($output | Out-String).Trim() } else { '(no output captured)' }
		throw "$Operation failed (docker exit $LASTEXITCODE): $message"
	}

	return $output
}

function Get-DeploymentOutput {
	param(
		[Parameter(Mandatory)][string]$Name
	)
	$value = az deployment group show `
		-g $ResourceGroup `
		-n $DeploymentName `
		--query "properties.outputs.$Name.value" `
		-o tsv 2>&1
	if ($LASTEXITCODE -ne 0) {
		throw "Could not read deployment output '$Name' from $DeploymentName in $ResourceGroup. az said: $($value | Out-String)"
	}

	if ([string]::IsNullOrWhiteSpace($value)) {
		throw "Deployment output '$Name' was empty. The deployment likely did not finish -- re-run the script."
	}

	return $value.Trim()
}

# ---------------------------------------------------------------------------
# Prerequisites
# ---------------------------------------------------------------------------
Write-Step 'Validating prerequisites'

Assert-Tool -Name 'az'
Assert-Tool -Name 'docker'
Assert-DockerRunning

if (-not (Test-Path $bicepDir)) {
	throw "Could not locate deploy/bicep at $bicepDir. Run this script from a clone of the AI Librarian repo."
}

if (-not (Test-Path (Join-Path $bicepDir 'main-pilot.bicep'))) {
	throw "main-pilot.bicep missing under $bicepDir."
}

if (-not (Test-Path (Join-Path $changelogDir 'master.xml'))) {
	throw "Liquibase master.xml missing under $changelogDir."
}

Write-Host 'OK -- az + docker present, daemon reachable, repo layout looks right.'

# ---------------------------------------------------------------------------
# Password
# ---------------------------------------------------------------------------
if (-not $Password) {
	Write-Step 'PostgreSQL admin password'
	$Password = Read-Host -AsSecureString -Prompt 'Enter a strong Postgres admin password (will not be echoed)'
}

$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
try {
	$plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
}
finally {
	[System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

# Surface to child processes (psql, liquibase). Cleared in `finally` below.
$Env:AILIB_PG_ADMIN_PASSWORD = $plainPassword

try {
	# -----------------------------------------------------------------------
	# Resource group (idempotent)
	# -----------------------------------------------------------------------
	Write-Step "Resource group: $ResourceGroup in $Location"

	$rgExists = (az group exists -n $ResourceGroup) -eq 'true'
	if (-not $rgExists) {
		az group create -n $ResourceGroup -l $Location | Out-Null
		Write-Host "Created."
	}
	else {
		Write-Host 'Already exists; reusing.'
	}

	# -----------------------------------------------------------------------
	# What-if (always) then deploy (unless -WhatIfOnly)
	# -----------------------------------------------------------------------
	Write-Step 'Bicep what-if'
	az deployment group what-if `
		--resource-group $ResourceGroup `
		--template-file (Join-Path $bicepDir 'main-pilot.bicep') `
		--parameters (Join-Path (Join-Path $bicepDir 'parameters') 'pilot.bicepparam') `
		--parameters "postgresAdminPassword=$plainPassword" `
		--parameters "location=$Location"

	if ($WhatIfOnly) {
		Write-Host ''
		Write-Host '-WhatIfOnly: stopping before deploy.' -ForegroundColor Yellow
		return
	}

	Write-Step "Bicep deploy: $DeploymentName"

	# Retry on transient ServerIsBusy -- Postgres Flexible Server can only
	# process one configuration write at a time, and the azure.extensions
	# config (which installs pgvector) sometimes collides with background
	# server initialization. Waiting and retrying clears it.
	#
	# Explicit $LASTEXITCODE checks because Windows PowerShell 5.1 doesn't
	# honor $PSNativeCommandUseErrorActionPreference; bare `az` failures
	# don't propagate to the caller without this.
	$maxDeployAttempts = 4
	$deployAttempt = 0
	$deploySucceeded = $false
	$paramFile = Join-Path (Join-Path $bicepDir 'parameters') 'pilot.bicepparam'
	$templateFile = Join-Path $bicepDir 'main-pilot.bicep'

	while (-not $deploySucceeded -and $deployAttempt -lt $maxDeployAttempts) {
		$deployAttempt++
		Write-Host "  Attempt $deployAttempt/$maxDeployAttempts..."

		$deployOutput = az deployment group create `
			--resource-group $ResourceGroup `
			--name $DeploymentName `
			--template-file $templateFile `
			--parameters $paramFile `
			--parameters "postgresAdminPassword=$plainPassword" `
			--parameters "location=$Location" 2>&1

		if ($LASTEXITCODE -eq 0) {
			$deploySucceeded = $true
			break
		}

		$outputText = ($deployOutput | Out-String)
		if ($outputText -match 'ServerIsBusy' -and $deployAttempt -lt $maxDeployAttempts) {
			$waitSeconds = 60 * $deployAttempt
			Write-Host "  Postgres reports ServerIsBusy; waiting ${waitSeconds}s before retry..." -ForegroundColor Yellow
			Start-Sleep -Seconds $waitSeconds
			continue
		}

		throw "Bicep deployment '$DeploymentName' failed on attempt $deployAttempt and the error is not retryable. Full output:`n$outputText"
	}

	if (-not $deploySucceeded) {
		throw "Bicep deployment '$DeploymentName' did not succeed after $maxDeployAttempts attempts (all transient ServerIsBusy). Wait 5+ minutes and re-run; the slim pilot is idempotent."
	}

	# -----------------------------------------------------------------------
	# Capture outputs
	# -----------------------------------------------------------------------
	Write-Step 'Reading deployment outputs'
	$postgresFqdn = Get-DeploymentOutput -Name 'postgresFqdn'
	$postgresServerName = Get-DeploymentOutput -Name 'postgresServerName'
	$storageAccountName = Get-DeploymentOutput -Name 'storageAccountName'
	$serviceBusNamespaceName = Get-DeploymentOutput -Name 'serviceBusNamespaceName'
	$serviceBusIngestQueue = Get-DeploymentOutput -Name 'serviceBusIngestQueueName'

	Write-Host "  postgresFqdn            : $postgresFqdn"
	Write-Host "  postgresServerName      : $postgresServerName"
	Write-Host "  storageAccountName      : $storageAccountName"
	Write-Host "  serviceBusNamespaceName : $serviceBusNamespaceName"
	Write-Host "  serviceBusIngestQueue   : $serviceBusIngestQueue"

	# -----------------------------------------------------------------------
	# Allow this workstation's public IP through the Postgres firewall.
	# The freshly-deployed Flexible Server only allows "Azure services";
	# the docker psql + liquibase containers run on the operator's machine
	# and hit Postgres from the operator's public NAT IP, which must be
	# allow-listed. Idempotent: same rule name every run, az upserts.
	# -----------------------------------------------------------------------
	Write-Step 'Configuring PostgreSQL firewall for this workstation'

	$myIp = $null
	try {
		$myIp = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10).Trim()
	}
	catch {
		throw "Could not detect this workstation's public IP via api.ipify.org. Add a firewall rule manually and re-run: az postgres flexible-server firewall-rule create -g $ResourceGroup -n $postgresServerName --rule-name operator-pilot-script --start-ip-address <your-ip> --end-ip-address <your-ip>"
	}

	$ruleName = 'operator-pilot-script'
	Write-Host "  Detected public IP: $myIp"
	Write-Host "  Upserting firewall rule: $ruleName"

	az postgres flexible-server firewall-rule create `
		--resource-group $ResourceGroup `
		--name $postgresServerName `
		--rule-name $ruleName `
		--start-ip-address $myIp `
		--end-ip-address $myIp `
		--output none

	# Azure typically propagates Flexible Server firewall changes in
	# under 30 seconds; give it 20 to be safe before psql attempts to
	# connect. A retry loop on the CREATE DATABASE call would be more
	# robust but adds complexity for a one-time delay.
	Write-Host '  Waiting 20s for firewall propagation...'
	Start-Sleep -Seconds 20

	# -----------------------------------------------------------------------
	# Application database (idempotent)
	# -----------------------------------------------------------------------
	Write-Step "Creating database '$DatabaseName' (if missing)"
	$pgConnAdmin = "host=$postgresFqdn port=5432 dbname=postgres user=ailibrarian password=$plainPassword sslmode=require"

	# Two-step idempotent create: pg_database lookup first, CREATE only if
	# absent. The original SELECT ... \gexec one-shot was psql-metacommand
	# territory and only works when psql reads SQL from stdin or -f file,
	# not from -c "string". Splitting into two calls avoids the dependency
	# on metacommand interpretation entirely.
	$checkRaw = Invoke-DockerOrFail `
		-Operation "check if $DatabaseName exists" `
		-DockerArgs @(
			'run', '--rm', 'postgres:16-alpine',
			'psql', $pgConnAdmin, '-tA',
			'-c', "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'"
		)

	$dbExists = $false
	if ($null -ne $checkRaw) {
		$checkText = ($checkRaw | Out-String).Trim()
		$dbExists = $checkText -eq '1'
	}

	if ($dbExists) {
		Write-Host "Database '$DatabaseName' already exists; skipping create."
	}
	else {
		Invoke-DockerOrFail `
			-Operation "CREATE DATABASE $DatabaseName" `
			-DockerArgs @(
				'run', '--rm', 'postgres:16-alpine',
				'psql', $pgConnAdmin, '-v', 'ON_ERROR_STOP=1',
				'-c', "CREATE DATABASE $DatabaseName"
			) | Out-Null
		Write-Host "Database '$DatabaseName' created."
	}

	# -----------------------------------------------------------------------
	# Liquibase migrations (idempotent via DATABASECHANGELOG)
	# -----------------------------------------------------------------------
	if (-not $SkipMigrations) {
		Write-Step 'Applying Liquibase changelog (db/changelog/master.xml)'
		Invoke-DockerOrFail `
			-Operation 'Liquibase update' `
			-DockerArgs @(
				'run', '--rm',
				'-v', "${changelogDir}:/liquibase/changelog",
				'liquibase/liquibase:4.29',
				"--url=jdbc:postgresql://${postgresFqdn}:5432/${DatabaseName}?sslmode=require",
				'--username=ailibrarian',
				"--password=$plainPassword",
				'--changeLogFile=changelog/master.xml',
				'update'
			) | Out-Null

		# Verify 0100 actually applied -- this was the source-shares RLS
		# recursion fix; a forgotten include in master.xml would leave the
		# database in a state where every query 42P17s. Belt-and-suspenders.
		Write-Step 'Verifying 0100 RLS recursion fix is live'
		$verifySql = "SELECT CASE WHEN qual LIKE '%FROM sources%' THEN 'BROKEN: 0100 missing' ELSE 'ok' END FROM pg_policies WHERE tablename = 'source_shares' AND policyname = 'p_source_shares_read';"
		$pgConnApp = "host=$postgresFqdn port=5432 dbname=$DatabaseName user=ailibrarian password=$plainPassword sslmode=require"
		$verifyRaw = Invoke-DockerOrFail `
			-Operation 'pg_policies probe' `
			-DockerArgs @(
				'run', '--rm', 'postgres:16-alpine',
				'psql', $pgConnApp, '-t', '-A', '-c', $verifySql
			)
		$verifyResult = if ($null -ne $verifyRaw) { ($verifyRaw | Out-String).Trim() } else { '' }
		if ($verifyResult -ne 'ok') {
			throw "0100 RLS recursion fix did not land in source_shares.p_source_shares_read (got '$verifyResult'). pg_policies.qual still references sources; aborting before seed. Check that db/changelog/master.xml includes 0100-fix-source-shares-rls-recursion.sql."
		}
		Write-Host 'OK -- p_source_shares_read is non-recursive.'
	}
	else {
		Write-Step '-SkipMigrations: not running Liquibase.'
		$pgConnApp = "host=$postgresFqdn port=5432 dbname=$DatabaseName user=ailibrarian password=$plainPassword sslmode=require"
	}

	# -----------------------------------------------------------------------
	# Seed Engineering pilot department + user
	# -----------------------------------------------------------------------
	Write-Step 'Seeding Engineering pilot department + test contributor'

	# Heredoc-style SQL; SET app.is_admin = true so RLS write predicates
	# pass during seed. The seeded user's is_employee flag is true so the
	# Internal-read path works for them after sign-in.
	$seedSql = @'
SET app.is_authenticated = 'true';
SET app.is_employee = 'true';
SET app.is_admin = 'true';
SET app.user_id = '00000000-0000-0000-0000-00000000ffff';

INSERT INTO departments (id, name, display_name)
VALUES ('11111111-1111-1111-1111-111111111111', 'engineering', 'Engineering')
ON CONFLICT (name) DO NOTHING;

INSERT INTO users (id, email, display_name, is_employee)
VALUES ('22222222-2222-2222-2222-222222222222', 'pilot.engineer@example.com', 'Pilot Engineer', true)
ON CONFLICT (id) DO NOTHING;

INSERT INTO user_authorizations (user_id, department_id, role, source_group_id)
VALUES ('22222222-2222-2222-2222-222222222222',
        '11111111-1111-1111-1111-111111111111',
        'Contributor',
        'pilot-local')
ON CONFLICT DO NOTHING;
'@

	# Stream the SQL into a containerized psql via stdin so we don't have
	# to mount a temp file across the Docker boundary.
	Invoke-DockerOrFail `
		-Operation 'Seed Engineering pilot' `
		-Stdin $seedSql `
		-DockerArgs @(
			'run', '--rm', '-i', 'postgres:16-alpine',
			'psql', $pgConnApp, '-v', 'ON_ERROR_STOP=1'
		) | Out-Null
	Write-Host 'Seed applied.'

	# -----------------------------------------------------------------------
	# Connection strings -> deploy/pilot.local.env
	# -----------------------------------------------------------------------
	Write-Step 'Pulling connection strings'

	$storageConn = (az storage account show-connection-string `
		-g $ResourceGroup `
		-n $storageAccountName `
		--query connectionString -o tsv).Trim()

	$serviceBusConn = (az servicebus namespace authorization-rule keys list `
		-g $ResourceGroup `
		--namespace-name $serviceBusNamespaceName `
		--name RootManageSharedAccessKey `
		--query primaryConnectionString -o tsv).Trim()

	$pgConnString = "Host=$postgresFqdn;Port=5432;Database=$DatabaseName;Username=ailibrarian;Password=$plainPassword;Ssl Mode=Require;Trust Server Certificate=true"

	Write-Step "Writing $envOut"
	if (-not (Test-Path $envTemplate)) {
		throw "Env template missing at $envTemplate. Cannot derive output env file."
	}

	# Start from the template, substitute the placeholders. Keeps the
	# canonical list of keys in one place (the template) and resists drift.
	$envContent = (Get-Content $envTemplate -Raw) `
		-replace [regex]::Escape('<postgres-fqdn>'), $postgresFqdn `
		-replace [regex]::Escape('<password>'), $plainPassword `
		-replace [regex]::Escape('<storage-connection-string>'), $storageConn `
		-replace [regex]::Escape('<service-bus-connection-string>'), $serviceBusConn

	# Replace the full ConnectionStrings__Postgres line so any future
	# template change to the password-substitution shape stays correct.
	$envContent = $envContent `
		-replace 'ConnectionStrings__Postgres=.*', "ConnectionStrings__Postgres=$pgConnString" `
		-replace 'IngestWorker__Database__ConnectionString=.*', "IngestWorker__Database__ConnectionString=$pgConnString"

	# Write LF-only so PowerShell shells reading via Get-Content split correctly.
	[System.IO.File]::WriteAllText($envOut, $envContent.Replace("`r`n", "`n"))
	Write-Host "Wrote $envOut (gitignored)."

	# -----------------------------------------------------------------------
	# Next steps
	# -----------------------------------------------------------------------
	Write-Step 'Done -- next steps'
	Write-Host @"

Load env into your shell:

  Get-Content $envOut |
    Where-Object { `$_ -and -not `$_.StartsWith('#') } |
    ForEach-Object {
      `$n, `$v = `$_.Split('=', 2)
      Set-Item "env:`$n" `$v
    }

Run the three local apps (separate terminals):

  dotnet run --project src/AiLibrarian.Api
  dotnet run --project src/AiLibrarian.Portal
  dotnet run --project src/AiLibrarian.IngestWorker

Smoke test:

  1. Browse http://localhost:5215 and upload a Markdown / DOCX / XLSX / PPTX file.
  2. Use departmentId=11111111-1111-1111-1111-111111111111 and
     contributorId=22222222-2222-2222-2222-222222222222 if not pre-filled.
  3. POST the returned blobUri+sourceId to /api/ingest/enqueue, then
     watch the worker log for `ingest.canonicalized` and `ingest.embedded`.
  4. Confirm: psql ... -c "SELECT event_type, event_subtype, count(*)
     FROM audit_events GROUP BY 1,2 ORDER BY 1,2;"

Tear down (disposable pilot):

  az group delete -n $ResourceGroup --yes
"@
}
finally {
	# Scrub the admin password from the process env immediately. The env
	# file on disk still has it (by design -- the apps need it); operators
	# control that file with filesystem permissions.
	$Env:AILIB_PG_ADMIN_PASSWORD = $null
	Remove-Variable plainPassword -ErrorAction SilentlyContinue
}
