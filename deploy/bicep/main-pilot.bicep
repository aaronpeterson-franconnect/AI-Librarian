// AI Librarian — slim pilot footprint (data plane only).
// Deploys: PostgreSQL Flexible + Blob Storage + Service Bus (Basic).
// Omits: Log Analytics, Application Insights, Key Vault, managed identity,
// Container Apps environment, and all RBAC (apps use connection strings from Key Vault / portal for now).
//
//   az deployment group create -g <rg> -f deploy/bicep/main-pilot.bicep \
//     -p deploy/bicep/parameters/pilot.bicepparam \
//     -p postgresAdminPassword='<secure>'
//
// Graduate to deploy/bicep/main.bicep when you need hosted API (ACA), MI + RBAC, or central secrets.

targetScope = 'resourceGroup'

@minLength(2)
@maxLength(10)
@description('Short name prefix used inside resource names (Storage, Postgres, etc.).')
param namePrefix string = 'ailib'

@description('Azure region for every resource in this deployment.')
param location string = resourceGroup().location

@description('Environment label applied as a tag (pilot / dev / stage / prod).')
param environment string = 'pilot'

@description('PostgreSQL flexible server administrator login.')
param postgresAdminLogin string = 'ailibrarian'

@secure()
@description('PostgreSQL administrator password — supply via CLI or CI secret; never commit.')
param postgresAdminPassword string

@description('Burstable SKU for PostgreSQL Flexible Server.')
param postgresSkuName string = 'Standard_B1ms'

@description('SKU tier for PostgreSQL Flexible Server.')
param postgresSkuTier string = 'Burstable'

var nameSuffix = uniqueString(resourceGroup().id, deployment().name)

var commonTags = {
	'audit-scope': 'ai-librarian'
	environment: environment
	workload: 'ai-librarian'
	deployment: 'pilot-data-plane'
}

var postgresServerName = take(toLower('psql-${namePrefix}-${nameSuffix}'), 63)

module storage 'modules/storage-blob.bicep' = {
	name: 'storage'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
	}
}

module serviceBus 'modules/service-bus.bicep' = {
	name: 'servicebus'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
		skuName: 'Basic'
		skuTier: 'Basic'
	}
}

module postgres 'modules/postgres-flexible.bicep' = {
	name: 'postgres'
	params: {
		location: location
		serverName: postgresServerName
		administratorLogin: postgresAdminLogin
		administratorLoginPassword: postgresAdminPassword
		tags: commonTags
		skuName: postgresSkuName
		skuTier: postgresSkuTier
	}
}

output nameSuffix string = nameSuffix

output storageAccountId string = storage.outputs.storageAccountId
output storageAccountName string = storage.outputs.storageAccountName
output blobEndpoint string = storage.outputs.blobEndpoint

output serviceBusNamespaceId string = serviceBus.outputs.namespaceId
output serviceBusNamespaceName string = serviceBus.outputs.namespaceName
output serviceBusHostname string = serviceBus.outputs.hostname
output serviceBusIngestQueueName string = serviceBus.outputs.ingestQueueName

output postgresServerId string = postgres.outputs.serverId
output postgresFqdn string = postgres.outputs.fqdn
output postgresServerName string = postgresServerName
output postgresAdministratorLogin string = postgres.outputs.administratorLoginOut
