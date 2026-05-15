// Production parameters — non-secret values only. Supply postgresAdminPassword at deploy time.
//
// Production should move PostgreSQL to private endpoints + Entra-only auth;
// those changes are a follow-up deployment, not this baseline file.
//
using '../main.bicep'

param location = 'eastus2'
param environment = 'prod'
param namePrefix = 'ailib'

param postgresSkuName = 'Standard_D2s_v3'
param postgresSkuTier = 'GeneralPurpose'

// Required by main.bicep (@secure()); read from env at compile time. Newer
// Bicep (>= 0.21) enforces BCP258 even when --parameters overrides this.
// Prod CI: surface this via a Key Vault-backed pipeline secret, never inline.
param postgresAdminPassword = readEnvironmentVariable('AILIB_PG_ADMIN_PASSWORD')
