# Pilot — fully cloud-hosted (graduated from operator-laptop)

> The slim pilot (`deploy/scripts/Deploy-Pilot.ps1` + `main-pilot.bicep`)
> deploys only the data plane (Postgres + Blob + Service Bus) and
> expects the operator to run the API, IngestWorker, and Portal locally.
> This runbook graduates the pilot to a fully cloud-hosted footprint:
> three Container Apps pulling images from ACR via managed identity.
>
> Use this when:
> - The operator-laptop dependency has become an availability risk.
> - You need shared portal access for >1 user.
> - You're ready to wire CI image push.

## What you get

| Layer | Today (slim pilot) | This runbook |
|------|---------|--------------|
| Postgres + pgvector | Flexible server | Flexible server (unchanged) |
| Blob storage | Storage Account | Storage Account (unchanged) |
| Service Bus | Basic namespace | Basic namespace (unchanged) |
| API | `dotnet run` on laptop | Container App, ingress public |
| IngestWorker | `dotnet run` on laptop | Container App, no ingress |
| Portal | `dotnet run` on laptop | Container App, ingress public |
| Registry | n/a | Azure Container Registry, Basic SKU |
| Image pulls | n/a | Workload identity → AcrPull RBAC |
| Log Analytics + App Insights | n/a | Workspace + AI resource (auto-wired) |
| Key Vault | n/a | Vault for connection strings |

`main.bicep` (not `main-pilot.bicep`) is the template for this runbook.
It already includes every module needed; the new bits are the ACR
module, AcrPull RBAC for the workload identity, and the
`-deployContainerRegistry true` parameter to opt in.

## Prerequisites

- The slim pilot has run successfully (`Deploy-Pilot.ps1` to completion)
  OR you are willing to wipe the resource group and start fresh.
- Docker Desktop running locally (image builds).
- Azure CLI logged in (`az login`), correct subscription
  (`az account set --subscription <id>`).
- A resource group named the same as your slim pilot RG (default
  `rg-ailib-pilot`) — `main.bicep` is idempotent over the existing
  data-plane resources.

## Steps

### 1. Set the postgres admin password env var

```powershell
$env:AILIB_PG_ADMIN_PASSWORD = '<the same password the slim pilot used>'
```

### 2. Build + push the three images

```powershell
# One-time: log in to your future registry. ACR doesn't exist yet —
# this fails until step 3. If you want to push BEFORE deploying bicep,
# create the ACR manually first or skip ahead to step 3 with empty
# image refs and re-run this script after.
.\deploy\scripts\Build-Images.ps1 -Tag v1   # local build first

# Verify locally
docker run --rm ailib-api:v1 --help    # any image responds OK to dotnet's startup
```

### 3. Deploy main.bicep with ACR enabled

```powershell
az deployment group create `
  --resource-group rg-ailib-pilot `
  --template-file deploy/bicep/main.bicep `
  --parameters deploy/bicep/parameters/pilot.bicepparam `
  --parameters postgresAdminPassword="$env:AILIB_PG_ADMIN_PASSWORD" `
  --parameters deployContainerRegistry=true
```

This creates the ACR. Outputs include `containerRegistryLoginServer`.

### 4. Push the images to ACR

```powershell
$acrLogin = az deployment group show `
  --resource-group rg-ailib-pilot `
  --name main `
  --query 'properties.outputs.containerRegistryLoginServer.value' -o tsv

az acr login --name ($acrLogin -replace '\.azurecr\.io$', '')

.\deploy\scripts\Build-Images.ps1 -Registry $acrLogin -Tag v1 -Push
```

### 5. Re-deploy main.bicep with the three Container Apps enabled

```powershell
$acrLogin = az deployment group show `
  --resource-group rg-ailib-pilot `
  --name main `
  --query 'properties.outputs.containerRegistryLoginServer.value' -o tsv

az deployment group create `
  --resource-group rg-ailib-pilot `
  --template-file deploy/bicep/main.bicep `
  --parameters deploy/bicep/parameters/pilot.bicepparam `
  --parameters postgresAdminPassword="$env:AILIB_PG_ADMIN_PASSWORD" `
  --parameters deployContainerRegistry=true `
  --parameters deployApiContainerApp=true `
  --parameters apiContainerImage="$acrLogin/ailib-api:v1" `
  --parameters deployIngestWorkerContainerApp=true `
  --parameters ingestWorkerContainerImage="$acrLogin/ailib-ingest:v1" `
  --parameters deployPortalContainerApp=true `
  --parameters portalContainerImage="$acrLogin/ailib-portal:v1" `
  --parameters portalApiBaseUrl="https://<api-fqdn-from-step-3>" `
  --parameters portalDefaultDepartmentId="<engineering dept GUID>"
```

Note the chicken-and-egg on `portalApiBaseUrl`: the API FQDN is an
output of the same deployment that creates the portal app. Two
workarounds:

- **Two-deploy approach** (cleanest): run step 5 with `deployPortalContainerApp=false` first,
  capture the API FQDN from outputs, then re-run with the portal enabled.
- **Single-deploy with placeholder**: pass a placeholder, then update
  the portal's environment variable post-deploy via
  `az containerapp update`.

### 6. Wire connection strings into Container Apps

The three workloads need:

| Workload | Env vars |
|---------|----------|
| API | `ConnectionStrings__Postgres`, `IngestQueue__ConnectionString`, `Storage__BlobConnectionString` |
| Worker | `ConnectionStrings__Postgres`, `IngestWorker__ServiceBus__ConnectionString`, `Storage__BlobConnectionString` |
| Portal | `Api__BaseUrl` (= API FQDN) |

Pilot path: pull from the deploy outputs and `az containerapp update --set-env-vars`.
Production path: Key Vault references on the container app secrets
section (`@Microsoft.KeyVault(SecretUri=...)`); Bicep wires this in
`main.bicep` once the connection strings land in Key Vault.

### 7. Verify

- `https://<portal-fqdn>/upload` opens the upload form.
- An upload produces a sources row AND drains through Service Bus
  to the worker (check audit_events).
- Worker logs in App Insights show `Ingest worker: listening on queue ingest-jobs with 5 skill(s).`
  (The 5th skill is the PDF skill from B.4.)

## CI image push

The repository ships a `Release Images` GitHub Actions workflow
(`.github/workflows/release-images.yml`) that builds and publishes
all three images automatically. Trigger shape:

| Trigger | Tags produced |
|---------|---------------|
| Push to `main` | `main`, `main-<short-sha>` |
| Push of `v*.*.*` tag | `<version>` (e.g. `1.2.3`), `latest` |
| `workflow_dispatch` | Same as the branch the dispatch ran on |

### Default: GHCR

The workflow always pushes to GitHub Container Registry under
`ghcr.io/<owner>/ailib-{api,ingest,portal}`. No secrets required —
`GITHUB_TOKEN` has the implicit `packages:write` permission the
workflow declares.

To pull from GHCR for Container Apps, you need to either:

1. **Make the GHCR packages public** (Settings → Packages → … →
   Change visibility). Container Apps will pull anonymously.
2. **Provision a GHCR-readable pull secret** for the Container Apps
   environment. See the Container Apps docs on private registry auth.

### Optional: dual-push to ACR

Set three repository secrets and the workflow mirrors every push into
your Azure Container Registry too:

| Secret | Value |
|--------|-------|
| `ACR_LOGIN_SERVER` | e.g. `acrailibxxxx.azurecr.io` (from `containerRegistryLoginServer` bicep output) |
| `ACR_USERNAME` | Service principal ID (or admin user) with `AcrPush` role |
| `ACR_PASSWORD` | Service principal client secret |

The workflow detects these and pushes the same tags to both registries
in one `docker/build-push-action` step, so cache stays warm.

To generate a service principal scoped to your ACR:

```powershell
$acrName = az acr list --resource-group rg-ailib-pilot --query '[0].name' -o tsv
$acrId   = az acr show --name $acrName --query id -o tsv

az ad sp create-for-rbac `
  --name "sp-ailib-ci-acr-push" `
  --scopes $acrId `
  --role AcrPush `
  --json-auth

# Use the .clientId as ACR_USERNAME, .clientSecret as ACR_PASSWORD.
```

## Rolling forward

Future updates to any of the three workloads:

```powershell
# Build + push one workload (after a code change)
.\deploy\scripts\Build-Images.ps1 -Registry <acr>.azurecr.io -Tag v2 -Workloads worker -Push

# Update the container app to the new tag
az containerapp update `
  --resource-group rg-ailib-pilot `
  --name ca-ingest-<suffix> `
  --image <acr>.azurecr.io/ailib-ingest:v2
```

The Container Apps platform handles zero-downtime revision swap.

## Tearing it down

`az group delete --name rg-ailib-pilot` removes everything in one
shot. Soft-delete on Key Vault and Postgres means the names stay
reserved for the SKU's purge window (7 days for KV, 7-35 for
Postgres); rename if you need to redeploy immediately.

## Known limitations

- **Public ingress.** Both API and Portal are publicly reachable. Phase
  4 networking work (private endpoints + VNet) gates production rollout.
- **No autoscale.** Container Apps min/max replicas are conservative
  defaults. Scale events should be sized after real traffic.
- **Image promotion is manual.** A CI image-push workflow is the
  natural follow-up — `Build-Images.ps1` is the script the workflow
  would call.
