// Cloud-hosted pilot — full Container Apps deploy (data plane + 3
// workloads). Sibling of pilot.bicepparam (slim/operator-laptop pilot).
//
// Used with main.bicep (NOT main-pilot.bicep). Image refs default to
// the GHCR-public path so deployContainerRegistry can stay false; the
// three Container App toggles are passed at the CLI rather than baked
// in here so the same param file works for "data plane only" and
// "data plane + apps" deploys without duplication.
//
// First deploy (data plane only -- waits for Liquibase before apps):
//
//   az deployment group create `
//     --resource-group rg-ailib-pilot `
//     --template-file deploy/bicep/main.bicep `
//     --parameters deploy/bicep/parameters/pilot-cloud.bicepparam `
//     --parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
//
// Second deploy (after Liquibase has run, turn on the apps):
//
//   az deployment group create `
//     --resource-group rg-ailib-pilot `
//     --template-file deploy/bicep/main.bicep `
//     --parameters deploy/bicep/parameters/pilot-cloud.bicepparam `
//     --parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD" `
//     --parameters deployApiContainerApp=true `
//     --parameters apiContainerImage='ghcr.io/<owner>/ailib-api:main' `
//     --parameters deployIngestWorkerContainerApp=true `
//     --parameters ingestWorkerContainerImage='ghcr.io/<owner>/ailib-ingest:main' `
//     --parameters deployPortalContainerApp=true `
//     --parameters portalContainerImage='ghcr.io/<owner>/ailib-portal:main' `
//     --parameters portalApiBaseUrl='https://<api-fqdn-from-first-deploy>'
//
// See docs/dev/deploy-pilot-cloud.md for the full sequence.

using '../main.bicep'

// Stable across pilot deploys -- match the slim-pilot file so data
// plane resources keep the same names if you switch back and forth.
param location = 'eastus2'
param environment = 'pilot'
param namePrefix = 'ailib'

// Postgres admin password is read from the AILIB_PG_ADMIN_PASSWORD env
// var at compile time (same pattern as pilot.bicepparam). The CLI's
// --parameters postgresAdminPassword=... overrides this; the env-var
// read keeps the param file BCP258-compliant when the CLI override is
// absent.
param postgresAdminPassword = readEnvironmentVariable('AILIB_PG_ADMIN_PASSWORD')

// No ACR for this pilot -- images come from public GHCR. The
// container-app modules don't declare a registries{} block, so a
// public registry path works without managed-identity RBAC. Flip to
// true if you later need private images (then provide ACR_LOGIN_SERVER
// + ACR_USERNAME + ACR_PASSWORD as GitHub secrets so Release Images
// dual-pushes to ACR).
param deployContainerRegistry = false

// Skip the data-plane RBAC role assignments (Key Vault Secrets User on
// the vault, Storage Blob Data Contributor on the account, Service Bus
// Data Owner on the namespace). Bicep writes these to grant the workload
// identity passwordless access to the data resources -- a prod-grade
// pattern that uses Key Vault references in the Container Apps secret
// section. But role-assignment writes require Owner or User Access
// Administrator on the subscription; a Contributor-only deployer (the
// common case for personal-sandbox pilots) can't perform them. The
// pilot uses connection strings wired via `az containerapp update
// --set-env-vars` (see docs/dev/deploy-pilot-cloud.md step 6), which
// doesn't need this RBAC. Flip back to true once the deployer is
// elevated and you're ready to migrate to KV-reference-based config.
param deployIdentityRbac = false
