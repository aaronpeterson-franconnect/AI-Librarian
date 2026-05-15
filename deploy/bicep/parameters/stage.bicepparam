// Staging parameters — non-secret values only. Supply postgresAdminPassword at deploy time.
//
using '../main.bicep'

param location = 'eastus2'
param environment = 'stage'
param namePrefix = 'ailib'

param postgresSkuName = 'Standard_B2s'
param postgresSkuTier = 'Burstable'

// Required by main.bicep (@secure()); read from env at compile time. Newer
// Bicep (>= 0.21) enforces BCP258 even when --parameters overrides this.
param postgresAdminPassword = readEnvironmentVariable('AILIB_PG_ADMIN_PASSWORD')
