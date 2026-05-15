// Dev parameters — non-secret values only.
//
// PostgreSQL administrator password is @secure() on main.bicep; pass at
// deploy time, for example:
//
//   az deployment group create -g rg-ai-librarian-dev \
//     -f deploy/bicep/main.bicep \
//     -p deploy/bicep/parameters/dev.bicepparam \
//     -p postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
//
using '../main.bicep'

param location = 'eastus2'
param environment = 'dev'
param namePrefix = 'ailib'

// Required by main.bicep (@secure()); read from env at compile time so the
// .bicepparam stays committable. Newer Bicep (>= 0.21) enforces BCP258 even
// when the CLI passes a --parameters override.
param postgresAdminPassword = readEnvironmentVariable('AILIB_PG_ADMIN_PASSWORD')
