// Key Vault — secrets, keys, and certificates for the platform. RBAC-only;
// grant the workload managed identity Key Vault Secrets User at deploy time.

@description('Azure region for Key Vault.')
param location string

@description('Stable suffix derived from resourceGroup().id (must yield a globally unique vault name).')
param nameSuffix string

@description('Resource tags.')
param tags object

// Key Vault name: 3-24 alphanumeric hyphens; must start with a letter.
var vaultName = take('kvailib${nameSuffix}', 24)

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
	name: vaultName
	location: location
	tags: tags
	properties: {
		sku: {
			family: 'A'
			name: 'standard'
		}
		tenantId: subscription().tenantId
		enableRbacAuthorization: true
		enabledForTemplateDeployment: false
		enabledForDiskEncryption: false
		enabledForDeployment: false
		publicNetworkAccess: 'Enabled'
	}
}

@description('Key Vault resource id.')
output vaultId string = keyVault.id

@description('Key Vault DNS hostname (https://.../).')
output vaultUri string = keyVault.properties.vaultUri

@description('Key Vault resource name.')
output vaultName string = keyVault.name
