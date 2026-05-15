// Slim pilot — data plane only (see deploy/bicep/main-pilot.bicep).
//
//   az deployment group what-if -g rg-ai-librarian-pilot \
//     -f deploy/bicep/main-pilot.bicep \
//     -p deploy/bicep/parameters/pilot.bicepparam \
//     -p postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
//
// Newer Bicep (>= 0.21) enforces BCP258: every parameter the template
// declares as required must also appear in the .bicepparam file, even
// when the CLI passes an override. We satisfy that by reading the
// PostgreSQL admin password from the AILIB_PG_ADMIN_PASSWORD env var
// at compile time. The Deploy-Pilot.ps1 script sets that env var; an
// interactive `az deployment group create -p ... .bicepparam` requires
// the operator to set it first.
using '../main-pilot.bicep'

param location = 'eastus2'
param environment = 'pilot'
param namePrefix = 'ailib'
param postgresAdminPassword = readEnvironmentVariable('AILIB_PG_ADMIN_PASSWORD')
