# Deploy — cloud-hosted pilot (GHCR variant)

Streamlined version of `deploy/runbooks/pilot-cloud-hosted.md`: skips the
ACR layer and pulls container images directly from public GHCR. Cuts
the full deploy to ~30 minutes once the prerequisite decisions are made.

This runbook does NOT cover the operator-laptop "slim pilot"
intermediate step — see `deploy/runbooks/pilot-cloud-hosted.md` for that
graduated path.

## Prerequisites

| # | Item | How to satisfy |
|---|---|---|
| 1 | Azure subscription with Contributor on a target RG | `az account set --subscription <id>` |
| 2 | Resource group | `az group create -n rg-ailib-pilot -l eastus2` |
| 3 | Postgres admin password — **see the password rules below before generating one** | `$env:AILIB_PG_ADMIN_PASSWORD = '<see below>'` |
| 4 | GHCR images set to public visibility | GitHub repo → Packages → each of `ailib-api`, `ailib-ingest`, `ailib-portal` → Package settings → Change visibility → Public |
| 5 | Docker Desktop running (only needed for Liquibase one-shot) | `docker --version` |
| 6 | Bicep CLI ≥ 0.21 | bundled with Azure CLI ≥ 2.50 |

The GHCR-public step (#4) is the one-time decision. Once flipped,
every future Release Images workflow run is automatically reachable
from Container Apps without re-clicking anything.

### Postgres password rules (read before generating)

The password flows through three layers, each with different forbidden
characters:

| Layer | Forbids |
|---|---|
| Azure Postgres Flexible Server | `/`, `\`, `"` |
| `cmd.exe` (az CLI wrapper on Windows) | `<`, `>`, `&`, `|`, `^`, `(`, `)`, `;`, `,`, `'`, `"`, `` ` ``, `?`, `*`, `[`, `]`, `{`, `}`, `~` |
| ADO.NET connection string parser | `;` (terminates value), `'`, `"` |

The intersection of "safe everywhere" is alphanumerics + `!@#%+=_-`. A
naive PowerShell generator over `ASCII 33..126` produces strings that
work in psql but break the deploy CLI with cryptic
`The syntax of the command is incorrect.` errors.

**Safe generator (alphanumerics + 8 symbols, 24 chars):**

```powershell
$chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#%+=_-'
$env:AILIB_PG_ADMIN_PASSWORD = -join ((1..24) | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
```

Or for a memorable sandbox value:

```powershell
$env:AILIB_PG_ADMIN_PASSWORD = 'PilotPg-2026-EastUs2-A1b'   # 24 chars, upper+lower+digit+dash, no shell metachars
```

Verify only safe symbols show up (everything alphanumeric masked to `X`):

```powershell
$env:AILIB_PG_ADMIN_PASSWORD -replace '[a-zA-Z0-9]', 'X'
# Expected: only `-`, `!`, `@`, `#`, `%`, `+`, `=`, `_` in the output -- NO `<`, `|`, `'`, `$`, `;`
```

Save the value somewhere durable (1Password etc.) — you'll need it for
the Liquibase step + the connection-string wire-up.

## The 7-step sequence

### 0. Log in + select subscription

```powershell
az login
az account set --subscription <your-subscription-id>
az account show --query '{name:name,id:id,tenant:tenantId}' -o table
```

### 1. Set env vars

```powershell
$env:AILIB_PG_ADMIN_PASSWORD = '<your 16+ char password>'
$RG = 'rg-ailib-pilot'
$REGION = 'eastus2'
$GHCR_OWNER = '<your github username or org>'    # e.g. aaronpeterson-franconnect
$IMAGE_TAG = 'main'                              # 'main' = latest commit; pin to 'main-<sha>' for reproducibility
```

### 2. Create the resource group + register providers

```powershell
az group create --name $RG --location $REGION

# Microsoft.App (Container Apps) is the one provider that's commonly
# unregistered in fresh subscriptions; the others (Web, Storage,
# ServiceBus, DBforPostgreSQL, KeyVault) are usually pre-registered.
# Registering an already-registered provider is a no-op.
az provider register --namespace Microsoft.App --wait
az provider register --namespace Microsoft.DBforPostgreSQL --wait
az provider register --namespace Microsoft.OperationalInsights --wait
```

### 2a. Bicep dry run (recommended)

```powershell
az deployment group what-if `
  --resource-group $RG `
  --template-file deploy/bicep/main.bicep `
  --parameters deploy/bicep/parameters/pilot-cloud.bicepparam
```

> **Important:** notice we do NOT pass `--parameters postgresAdminPassword=...`.
> The bicepparam file reads the password from
> `$env:AILIB_PG_ADMIN_PASSWORD` at compile time. Inlining the password
> in `--parameters` would route it through `cmd.exe` (which `az.cmd`
> wraps under the hood), and any shell metachar in the password trips
> `The syntax of the command is incorrect.` Reading it from the env
> var keeps `cmd.exe` out of the password path entirely.

What-if surfaces every resource Bicep would create; lets you cancel
before spending money if anything looks off (e.g., wrong region,
wrong namePrefix would clobber something).

### 3. First Bicep deploy (data plane only)

Brings up Postgres + ailibrarian database + Service Bus + Blob + Key
Vault + Container Apps Environment + Log Analytics + App Insights +
the workload-identity. The three Container Apps stay disabled
(default).

```powershell
az deployment group create `
  --resource-group $RG `
  --template-file deploy/bicep/main.bicep `
  --parameters deploy/bicep/parameters/pilot-cloud.bicepparam
```

~10 minutes. The Postgres flexible server is the long pole. The
`ailibrarian` database is created as a child resource of the server
(see `modules/postgres-flexible.bicep`); no separate
`az postgres flexible-server db create` step is required.

### 4. Run Liquibase against the Azure Postgres

The Postgres firewall only opens to "Azure services" by default — a
container running locally is blocked. Open it for your current public
IP so Liquibase can connect:

```powershell
$PG_NAME = az deployment group show -g $RG -n main `
  --query 'properties.outputs.postgresServerName.value' -o tsv
$PG_FQDN = az deployment group show -g $RG -n main `
  --query 'properties.outputs.postgresFqdn.value' -o tsv
$MY_IP = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()

az postgres flexible-server firewall-rule create `
  --resource-group $RG `
  --name $PG_NAME `
  --rule-name "DeployerLocal-$(Get-Date -Format 'yyyyMMdd')" `
  --start-ip-address $MY_IP `
  --end-ip-address $MY_IP
```

(`az` will warn about an upcoming flag rename in v2.86.0 — harmless.)

Then run Liquibase:

```powershell
docker run --rm `
  -v "${PWD}/db/changelog:/liquibase/changelog" `
  liquibase/liquibase:4.29 `
  --url="jdbc:postgresql://${PG_FQDN}:5432/ailibrarian?sslmode=require" `
  --username="ailibrarian" `
  --password="$Env:AILIB_PG_ADMIN_PASSWORD" `
  --changeLogFile="changelog/master.xml" `
  update
```

~30 seconds. Same image + same migrations the local compose stack
uses, so any "works on compose" guarantees translate to Azure here.

Verify it landed:

```powershell
docker run --rm `
  -v "${PWD}/db/changelog:/liquibase/changelog" `
  liquibase/liquibase:4.29 `
  --url="jdbc:postgresql://${PG_FQDN}:5432/ailibrarian?sslmode=require" `
  --username="ailibrarian" `
  --password="$Env:AILIB_PG_ADMIN_PASSWORD" `
  --changeLogFile="changelog/master.xml" `
  status
```

Expect: `ailibrarian@jdbc:... is up to date`.

### 5. Capture the API FQDN

The Portal needs the API's FQDN as its `Api:BaseUrl`. There's a
chicken-and-egg here — the API FQDN is an output of the same deploy
that creates the Portal. Solution: deploy the API first, then the
Portal in a second pass.

```powershell
# Sub-step 5a -- API + Worker only
az deployment group create `
  --resource-group $RG `
  --template-file deploy/bicep/main.bicep `
  --parameters deploy/bicep/parameters/pilot-cloud.bicepparam `
  --parameters deployApiContainerApp=true `
  --parameters apiContainerImage="ghcr.io/${GHCR_OWNER}/ailib-api:${IMAGE_TAG}" `
  --parameters deployIngestWorkerContainerApp=true `
  --parameters ingestWorkerContainerImage="ghcr.io/${GHCR_OWNER}/ailib-ingest:${IMAGE_TAG}"

# Capture the API FQDN. The deployment name `main` defaults from the
# template file name; pass --name explicitly if you ever override it.
$API_FQDN = az deployment group show -g $RG -n main `
  --query 'properties.outputs.apiContainerAppFqdn.value' -o tsv

# Sub-step 5b -- Portal with the captured FQDN
az deployment group create `
  --resource-group $RG `
  --template-file deploy/bicep/main.bicep `
  --parameters deploy/bicep/parameters/pilot-cloud.bicepparam `
  --parameters deployApiContainerApp=true `
  --parameters apiContainerImage="ghcr.io/${GHCR_OWNER}/ailib-api:${IMAGE_TAG}" `
  --parameters deployIngestWorkerContainerApp=true `
  --parameters ingestWorkerContainerImage="ghcr.io/${GHCR_OWNER}/ailib-ingest:${IMAGE_TAG}" `
  --parameters deployPortalContainerApp=true `
  --parameters portalContainerImage="ghcr.io/${GHCR_OWNER}/ailib-portal:${IMAGE_TAG}" `
  --parameters portalApiBaseUrl="https://${API_FQDN}"
```

~5 minutes per sub-step. Container Apps' revision system handles the
re-deploy without dropping traffic (no traffic to drop yet).

**Postgres "ServerIsBusy" retry:** the Postgres `azure.extensions`
configuration block sometimes flags a `ServerIsBusy` error on the
second deploy ("Cannot complete operation while server is busy
processing another operation"). It's transient -- Postgres is finishing
work from the first deploy. Re-run the same command; usually
succeeds on the second try. To check the server state directly:

```powershell
az postgres flexible-server show -g $RG -n $PG_NAME --query '{state:state}' -o table
# Wait for: State = Ready before re-running.
```

### 6. Wire connection strings into the Container Apps

The Bicep deploy creates the apps with the right managed-identity + env
plumbing, but the connection-string env vars are blank until we feed
them. Service Bus + Storage connection strings aren't Bicep outputs
(they contain secrets); pull them directly via `az` against the
resources Bicep just created.

```powershell
# Names from Bicep outputs (deterministic, derived from namePrefix + nameSuffix)
$SB_NS      = az deployment group show -g $RG -n main --query 'properties.outputs.serviceBusNamespaceName.value' -o tsv
$STG_NAME   = az deployment group show -g $RG -n main --query 'properties.outputs.storageAccountName.value' -o tsv
$API_APP    = az deployment group show -g $RG -n main --query 'properties.outputs.apiContainerAppName.value' -o tsv
$WORKER_APP = az deployment group show -g $RG -n main --query 'properties.outputs.ingestWorkerContainerAppName.value' -o tsv

# Postgres: build from FQDN + the password we already have
$PG_CONN = "Host=${PG_FQDN};Port=5432;Database=ailibrarian;Username=ailibrarian;Password=$Env:AILIB_PG_ADMIN_PASSWORD;SSL Mode=Require;Trust Server Certificate=true"

# Service Bus: pull the RootManageSharedAccessKey connection string. For
# production, scope down to a queue-specific SAS rule -- this RootManage
# key is fine for the pilot.
$SB_CONN = az servicebus namespace authorization-rule keys list `
  --resource-group $RG `
  --namespace-name $SB_NS `
  --name RootManageSharedAccessKey `
  --query primaryConnectionString -o tsv

# Storage: account-key connection string. Same prod-vs-pilot caveat as
# Service Bus -- prefer SAS-scoped credentials in production.
$BLOB_CONN = az storage account show-connection-string `
  --resource-group $RG `
  --name $STG_NAME `
  --query connectionString -o tsv

# API config-key layout (from src/AiLibrarian.Api/appsettings.json):
#   ConnectionStrings:Postgres -> Postgres
#   IngestQueue:ConnectionString -> Service Bus (used to enqueue jobs)
#   BlobStorage:ConnectionString -> Blob (uploads land here)
az containerapp update --name $API_APP --resource-group $RG --set-env-vars `
  "ConnectionStrings__Postgres=$PG_CONN" `
  "IngestQueue__ConnectionString=$SB_CONN" `
  "BlobStorage__ConnectionString=$BLOB_CONN"

# Worker config-key layout (from src/AiLibrarian.IngestWorker/appsettings.json
# -- intentionally different from the API: the worker namespaces its
# config under IngestWorker:* so the LlmGateway block can sit at the
# top level without colliding):
#   ConnectionStrings:Postgres  -> Postgres (aliased from
#                                  IngestWorker:Database:ConnectionString)
#   IngestWorker:ServiceBus:ConnectionString -> Service Bus (consumer)
#   IngestWorker:Blob:ConnectionString       -> Blob (skill plugins read here)
az containerapp update --name $WORKER_APP --resource-group $RG --set-env-vars `
  "ConnectionStrings__Postgres=$PG_CONN" `
  "IngestWorker__ServiceBus__ConnectionString=$SB_CONN" `
  "IngestWorker__Blob__ConnectionString=$BLOB_CONN"
```

The Portal already got `Api__BaseUrl` baked in during step 5b -- no
extra env-var pass needed for it.

### 7. Smoke

Same six assertions our local Live Smoke workflow runs against
docker-compose, now against the live cloud API.

> **PowerShell gotcha:** `curl` in PowerShell is aliased to
> `Invoke-WebRequest`, which **doesn't accept `-H` or `-d` flags**. Use
> `curl.exe` (ships with Windows 10+) for any POST with headers, or
> use `Invoke-RestMethod` natively. The GETs below work fine via the
> alias.

```powershell
$PORTAL_FQDN = az deployment group show -g $RG -n main `
  --query 'properties.outputs.portalContainerAppFqdn.value' -o tsv

# 1. /health -- expect HTTP 200 + audit circuit Closed (same shape as
#    local compose). Cloud equivalent of scripts/live-smoke.sh STEP 1.
curl "https://${API_FQDN}/health"

# 2. /api/audit/recent -- expect system.audit.writer.ready row, proving
#    the startup probe wrote to the cloud Postgres at boot.
curl "https://${API_FQDN}/api/audit/recent?limit=10"

# 3. Seed a department via psql (the deployer-local firewall rule from
#    step 4 still applies). `app.is_authenticated/is_employee=true`
#    push the RLS session context so the INSERT passes.
docker run --rm postgres:16 psql `
  "host=${PG_FQDN} port=5432 dbname=ailibrarian user=ailibrarian password=$Env:AILIB_PG_ADMIN_PASSWORD sslmode=require" `
  -c "SET app.is_authenticated = 'true'; SET app.is_employee = 'true'; INSERT INTO departments (id, name, display_name) VALUES ('11111111-1111-1111-1111-111111111111', 'engineering', 'Engineering') ON CONFLICT (name) DO NOTHING;"

# 4. /api/departments -- expect the seeded row to round-trip
curl "https://${API_FQDN}/api/departments"

# 5. /api/audit/recent again -- expect a departments.list event
curl "https://${API_FQDN}/api/audit/recent?limit=5"

# 6. /api/search/hybrid -- expect 503 with 'LlmGateway embedding provider'
#    in the body (the silent-degradation gate). USE curl.exe FOR POST.
curl.exe -i -X POST "https://${API_FQDN}/api/search/hybrid" `
  -H "Content-Type: application/json" `
  -d '{\"query\":\"smoke\"}'

# 7. Open the Portal in a browser; the Sources page should show the
#    Engineering department after the seed above.
Start-Process "https://${PORTAL_FQDN}"
```

If `/health` returns `audit.circuitState=Closed` and the Portal home
renders, the data plane + audit + Postgres wiring are all green
against Azure. Same proof shape as the local Live Smoke workflow.

## Federated identity setup (for hands-free CI deploys)

`.github/workflows/deploy.yml` deploys to this pilot RG on every push
to `main`, but only after a one-time OIDC federated-credential setup.
Without it, the workflow errors at `azure/login` with "no matching
federated identity record". Run these once per repo:

```powershell
$SUB = '709205f3-3395-4dec-9bc9-3f55bc8ec770'
$RG  = 'rg-ailib-pilot'
$APP_NAME = 'gha-ailib-deploy'
$REPO = 'aaronpeterson-franconnect/AI-Librarian'

# 1. Create the app registration + service principal
$APP_ID = az ad app create --display-name $APP_NAME --query appId -o tsv
az ad sp create --id $APP_ID
$SP_OID = az ad sp show --id $APP_ID --query id -o tsv

# 2. Grant Contributor on the pilot RG (NOT subscription -- least
#    privilege; the SP can only touch this one RG).
az role assignment create `
  --assignee $SP_OID `
  --role Contributor `
  --scope "/subscriptions/$SUB/resourceGroups/$RG"

# 3. Federated credential: trust GitHub Actions tokens for pushes to
#    main on this repo. Subject claim format is documented at
#    https://docs.github.com/en/actions/deployment/security-hardening-your-deployments/about-security-hardening-with-openid-connect
az ad app federated-credential create --id $APP_ID --parameters @"
{
  "name": "gha-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:$REPO`:ref:refs/heads/main",
  "description": "GitHub Actions push to main",
  "audiences": ["api://AzureADTokenExchange"]
}
"@

# 4. (Optional but recommended) Add a second federated credential for
#    the workflow_dispatch path -- triggers a deploy from the Actions
#    UI without a code change. Same SP, different subject claim.
az ad app federated-credential create --id $APP_ID --parameters @"
{
  "name": "gha-dispatch",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:$REPO`:environment:pilot",
  "description": "GitHub Actions workflow_dispatch via pilot environment",
  "audiences": ["api://AzureADTokenExchange"]
}
"@

# 5. Capture the IDs the workflow needs
Write-Host "AZURE_CLIENT_ID:       $APP_ID"
Write-Host "AZURE_TENANT_ID:       $(az account show --query tenantId -o tsv)"
Write-Host "AZURE_SUBSCRIPTION_ID: $SUB"
```

Then add those three values as **GitHub repository secrets** (Settings
→ Secrets and variables → Actions → New repository secret):

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | `$APP_ID` from step 5 |
| `AZURE_TENANT_ID` | tenant id from step 5 |
| `AZURE_SUBSCRIPTION_ID` | `709205f3-3395-4dec-9bc9-3f55bc8ec770` |
| `AILIB_PG_ADMIN_PASSWORD` | the same password you set in `$env:AILIB_PG_ADMIN_PASSWORD` for the manual deploy |

The Liquibase step in the deploy workflow needs the Postgres admin
password; it's a GitHub secret rather than pulling from Key Vault
because (a) the slim-pilot identity-RBAC story isn't wired yet
(`deployIdentityRbac=false`) and (b) rotating it via secret update +
re-deploy is a simpler operator story than KV-rotation-then-Container-
Apps-restart.

Also create a **GitHub Environment** named `pilot`:
1. Settings → Environments → New environment → `pilot`
2. (Optional) Required reviewers: yourself, if you want a manual
   "approve deploy" gate per workflow run.
3. The workflow references this environment via `environment: pilot`,
   which is also part of the federated credential's subject claim for
   `workflow_dispatch`.

Once setup is complete, the next push to `main` triggers a deploy.
Watch progress via `gh run watch` or in the Actions tab.

## Tear down

```powershell
az group delete --name rg-ailib-pilot --yes --no-wait
```

Key Vault soft-delete keeps the vault name reserved for ~7 days. If
you need to redeploy immediately, pass a different `namePrefix` in
`pilot-cloud.bicepparam`.

## What `deployIdentityRbac=false` defers

`pilot-cloud.bicepparam` ships with `deployIdentityRbac=false`. This
skips three role-assignment writes Bicep would otherwise perform: KV
Secrets User on the vault, Storage Blob Data Contributor on the account,
Service Bus Data Owner on the namespace. Granting them requires the
deploying principal to hold Owner or User Access Administrator on the
subscription -- Contributor isn't enough by Azure's role-separation
design.

For the pilot you don't need them: the runbook wires every workload
via connection strings in step 6, which works without any data-plane
RBAC on the managed identity. The trade-off is that you can't yet
move Container App config to Key Vault references (the prod-grade
pattern that hides connection strings behind `@Microsoft.KeyVault(...)`
syntax) -- when you do migrate to KV references, you'll need to flip
`deployIdentityRbac` to true and have someone elevate the deployer to
Owner/UAA at that point.

## What's NOT in this pilot

- **Azure OpenAI** — deliberately deferred for the first smoke. `/api/search/hybrid` and `/api/ask` return 503 until LLM keys are wired. To enable, add a `LlmGateway__Providers__azure-openai__*` env-var pass on the API container app after Azure OpenAI provisioning completes (24-72h lead time depending on region).
- **Entra auth** — dev-mode user context is auto-selected because `AzureAd:ClientId` is empty. To enable, follow `deploy/runbooks/entra-app-registrations.md` and add the `AzureAd__*` env vars to the API container app.
- **Private networking** — Postgres + Container Apps are public-ingress per the runbook's "Known limitations" section. Private endpoints are Phase 4.
- **Branch protection on `main`** — direct pushes can ship code straight to GHCR. Worth adding before adding additional operators.

## Troubleshooting

**"Image pull error" on a Container App:** GHCR package isn't public. Re-check step #4 in Prerequisites; the visibility setting applies per-package, not per-account.

**`/health` returns `audit.circuitState=Open` or 503:** the audit-writer startup probe couldn't connect to Postgres. Check `Auditing__WriterMode=Postgres` is set, and that `ConnectionStrings__Postgres` includes `SSL Mode=Require`.

**Bicep deploy says "the resource type Microsoft.App/managedEnvironments is not registered":** `az provider register --namespace Microsoft.App` then wait ~5 min.

**Liquibase fails with `relation "databasechangelog" does not exist`:** Postgres is reachable but the `ailibrarian` database doesn't exist yet. The Bicep postgres-flexible module creates it; if the deploy succeeded the DB exists -- re-check the connection string fqdn and port.

**Liquibase fails with `FATAL: database "ailibrarian" does not exist`:** older Bicep deploys (before the database resource was folded into `modules/postgres-flexible.bicep`) created the server without the database. Run `az postgres flexible-server db create --resource-group $RG --server-name $PG_NAME --database-name ailibrarian` once, then retry Liquibase. Re-deploys via the current `main.bicep` will create it automatically next time.

**`az deployment group what-if` returns `The syntax of the command is incorrect.`:** your `$env:AILIB_PG_ADMIN_PASSWORD` contains a `cmd.exe` metacharacter (`<`, `>`, `&`, `|`, `^`, `'`, etc.) AND you're passing `--parameters postgresAdminPassword=$Env:...` on the CLI. Two fixes: (a) drop the `--parameters` line for the password — the `pilot-cloud.bicepparam` reads the env var directly; (b) regenerate the password using the safe-charset generator in the "Postgres password rules" section above.

**`curl` (PowerShell alias for `Invoke-WebRequest`) errors with "Cannot bind parameter 'Headers'":** you tried to use the alias for a POST with `-H`. The alias doesn't accept the `-H`/`-d` curl flags. Use `curl.exe` (the real Windows curl) for any POST with headers.

**`Authorization failed ... 'Microsoft.Authorization/roleAssignments/write'`:** the deployer is `Contributor` only, not `Owner`/`User Access Administrator`. Either ask someone with Owner to grant you Owner, or keep `deployIdentityRbac=false` in `pilot-cloud.bicepparam` (already the default in this file).

**Bicep deploy of `azure.extensions` fails with `ServerIsBusy`:** transient. Postgres is finishing work from the previous deploy. Wait ~30s, check `az postgres flexible-server show -g $RG -n $PG_NAME --query state -o tsv` returns `Ready`, then re-run.
