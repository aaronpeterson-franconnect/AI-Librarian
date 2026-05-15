// Storage account + blob containers. Versioning + immutability policy (Unlocked)
// document the WORM path per ADR 0008 / phasing; operators lock in production.

@description('Azure region for the storage account.')
param location string

@description('Stable suffix derived from resourceGroup().id (used inside the account name).')
param nameSuffix string

@description('Resource tags.')
param tags object

// Storage account names: 3-24 lowercase letters and numbers only.
// Prefix is fixed length so the account name always satisfies the minimum length.
var storageAccountName = take(toLower('stailib${nameSuffix}'), 24)

resource storage 'Microsoft.Storage/storageAccounts@2023-04-01' = {
	name: storageAccountName
	location: location
	tags: tags
	sku: {
		name: 'Standard_LRS'
	}
	kind: 'StorageV2'
	properties: {
		minimumTlsVersion: 'TLS1_2'
		allowBlobPublicAccess: false
		supportsHttpsTrafficOnly: true
		allowSharedKeyAccess: true
		encryption: {
			services: {
				blob: {
					enabled: true
				}
				file: {
					enabled: true
				}
			}
			keySource: 'Microsoft.Storage'
		}
		networkAcls: {
			defaultAction: 'Allow'
		}
	}
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-04-01' = {
	name: 'default'
	parent: storage
	properties: {
		deleteRetentionPolicy: {
			enabled: true
			days: 7
		}
		containerDeleteRetentionPolicy: {
			enabled: true
			days: 7
		}
		isVersioningEnabled: true
		changeFeed: {
			enabled: false
		}
	}
}

resource sourcesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-04-01' = {
	name: 'sources'
	parent: blobServices
	properties: {
		publicAccess: 'None'
		immutableStorageWithVersioning: {
			enabled: true
		}
	}
}

// Time-based immutability in Unlocked state — satisfies "WORM path exists"
// without locking operators out during early development.
// Immutability policy: omit `state` (read-only on create). Operators move to
// Locked in production after validation. See deploy/README.md WORM notes.
resource sourcesImmutability 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-04-01' = {
	name: 'default'
	parent: sourcesContainer
	properties: {
		immutabilityPeriodSinceCreationInDays: 1
		allowProtectedAppendWrites: false
	}
}

@description('Storage account resource id.')
output storageAccountId string = storage.id

@description('Blob endpoint URL (https://<account>.blob.core.windows.net/).')
output blobEndpoint string = storage.properties.primaryEndpoints.blob

@description('Storage account name (for connection strings / MI RBAC).')
output storageAccountName string = storage.name
