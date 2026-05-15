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
| 3 | Postgres admin password (≥16 chars, complex) | `$env:AILIB_PG_ADMIN_PASSWORD = '<generated>'` |
| 4 | GHCR images set to public visibility | GitHub repo → Packages → each of `ailib-api`, `ailib-ingest`, `ailib-portal` → Package settings → Change visibility → Public |
| 5 | Docker Desktop running (only needed for Liquibase one-shot) | `docker --version` |
| 6 | Bicep CLI ≥ 0.21 | bundled with Azure CLI ≥ 2.50 |

The GHCR-public step (#4) is the one-time decision. Once flipped,
every future Release Images workflow run is automatically reachable
from Container Apps without re-clicking anything.

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
  --parameters deploy/bicep/parameters/pilot-cloud.bicepparam `
  --parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
```

Surfaces every resource Bicep would create; lets you cancel before
spending money if anything looks off (e.g., wrong region, wrong
namePrefix would clobber something).

### 3. First Bicep deploy (data plane only)

Brings up Postgres + Service Bus + Blob + Key Vault + Container Apps
Environment + Log Analytics + App Insights + the workload-identity. The
three Container Apps stay disabled (default).

```powershell
az deployment group create `
  --resource-group $RG `
  --template-file deploy/bicep/main.bicep `
  --parameters deploy/bicep/parameters/pilot-cloud.bicepparam `
  --parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
```

~10 minutes. The Postgres flexible server is the long pole.

### 4. Run Liquibase against the Azure Postgres

```powershell
$PG_FQDN = az deployment group show -g $RG -n main `
  --query 'properties.outputs.postgresFqdn.value' -o tsv

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
  --parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD" `
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
  --parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD" `
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

```powershell
$PORTAL_FQDN = az deployment group show -g $RG -n main `
  --query 'properties.outputs.portalContainerAppFqdn.value' -o tsv

# Health -- expect HTTP 200 + audit circuit Closed (same shape as local
# compose). This is the cloud equivalent of scripts/live-smoke.sh STEP 1.
curl "https://${API_FQDN}/health"

# Departments -- expect empty {"items":[]}
curl "https://${API_FQDN}/api/departments"

# Open the Portal
Start-Process "https://${PORTAL_FQDN}"
```

If `/health` returns `audit.circuitState=Closed` and the Portal home
renders, the data plane + audit + Postgres wiring are all green
against Azure. Same proof shape as the local Live Smoke workflow.

### 8 (optional). Seed a pilot department

```powershell
docker run --rm `
  postgres:16 psql `
  "host=${PG_FQDN} port=5432 dbname=ailibrarian user=ailibrarian password=$Env:AILIB_PG_ADMIN_PASSWORD sslmode=require" `
  -c "SET app.is_authenticated = 'true'; SET app.is_employee = 'true'; INSERT INTO departments (id, name, display_name) VALUES ('11111111-1111-1111-1111-111111111111', 'engineering', 'Engineering') ON CONFLICT (name) DO NOTHING;"

curl "https://${API_FQDN}/api/departments"
# → {"items":[{"id":"11111111-...","name":"engineering","displayName":"Engineering"}]}
```

## Tear down

```powershell
az group delete --name rg-ailib-pilot --yes --no-wait
```

Key Vault soft-delete keeps the vault name reserved for ~7 days. If
you need to redeploy immediately, pass a different `namePrefix` in
`pilot-cloud.bicepparam`.

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
