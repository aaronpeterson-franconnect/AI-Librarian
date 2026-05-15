// AI Librarian — Azure footprint (Phase 0).
// targetScope = resourceGroup — deploy with:
//   az deployment group create -g <rg> -f deploy/bicep/main.bicep \
//     -p deploy/bicep/parameters/dev.bicepparam \
//     -p postgresAdminPassword='<secure>'
//
// Entra app registrations are not expressible as first-class ARM resources;
// see deploy/runbooks/entra-app-registrations.md.

targetScope = 'resourceGroup'

@minLength(2)
@maxLength(10)
@description('Short name prefix used inside resource names (Storage, Postgres, etc.).')
param namePrefix string = 'ailib'

@description('Azure region for every resource in this deployment.')
param location string = resourceGroup().location

@description('Environment label applied as a tag (dev / stage / prod).')
param environment string = 'dev'

@description('PostgreSQL flexible server administrator login.')
param postgresAdminLogin string = 'ailibrarian'

@secure()
@description('PostgreSQL administrator password — supply via CLI or CI secret; never commit.')
param postgresAdminPassword string

@description('When true, deploy AiLibrarian.Api as a Container App (requires apiContainerImage).')
param deployApiContainerApp bool = false

@description('Container image for the API, e.g. myregistry.azurecr.io/ailib-api:tag (required when deployApiContainerApp is true).')
param apiContainerImage string = ''

@secure()
@description('Optional Application Insights connection string for the API container.')
param apiApplicationInsightsConnectionString string = ''

@description('When true, deploy AiLibrarian.IngestWorker as a Container App (requires ingestWorkerContainerImage).')
param deployIngestWorkerContainerApp bool = false

@description('Container image for the ingest worker, e.g. myregistry.azurecr.io/ailib-ingest:tag (required when deployIngestWorkerContainerApp is true).')
param ingestWorkerContainerImage string = ''

@description('When true, deploy AiLibrarian.Portal as a Container App (requires portalContainerImage and portalApiBaseUrl).')
param deployPortalContainerApp bool = false

@description('Container image for the portal, e.g. myregistry.azurecr.io/ailib-portal:tag (required when deployPortalContainerApp is true).')
param portalContainerImage string = ''

@description('Base URL the portal HttpClient calls into the API (e.g. https://ca-api-xxxxx.<env>.azurecontainerapps.io/). Required when deployPortalContainerApp is true.')
param portalApiBaseUrl string = ''

@description('Pre-filled department GUID for the portal upload form. Empty surfaces as a 400 on upload; operator sets per-environment after creating the pilot department.')
param portalDefaultDepartmentId string = ''

@description('Pre-filled classification on the portal upload form. Defaults to Internal per ADR 0011.')
param portalDefaultClassification string = 'Internal'

@description('Burstable SKU for PostgreSQL Flexible Server (dev default).')
param postgresSkuName string = 'Standard_B1ms'

@description('SKU tier for PostgreSQL Flexible Server.')
param postgresSkuTier string = 'Burstable'

@description('When true, deploy an Azure Container Registry alongside the workload identity. Container Apps pulls from this registry via the AcrPull RBAC role on the workload identity.')
param deployContainerRegistry bool = false

@allowed(['Basic', 'Standard', 'Premium'])
@description('Container Registry SKU. Pilot default Basic; Premium required for geo-replication / private endpoints.')
param containerRegistrySkuName string = 'Basic'

var nameSuffix = uniqueString(resourceGroup().id, deployment().name)

var commonTags = {
	'audit-scope': 'ai-librarian'
	environment: environment
	workload: 'ai-librarian'
}

// PostgreSQL server name: globally unique, 3-63, lowercase letters, numbers, hyphens.
var postgresServerName = take(toLower('psql-${namePrefix}-${nameSuffix}'), 63)

// Resource names duplicated here (must match modules) so RBAC roleAssignment
// `name` / `scope` are compile-time deterministic (BCP120).
var keyVaultName = take('kvailib${nameSuffix}', 24)
var storageAccountName = take(toLower('stailib${nameSuffix}'), 24)
var serviceBusNamespaceName = take('sb-ai-${nameSuffix}', 50)
var containerRegistryName = take(toLower('acrailib${nameSuffix}'), 50)

module law 'modules/log-analytics.bicep' = {
	name: 'law'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
	}
}

module appInsights 'modules/application-insights.bicep' = {
	name: 'appinsights'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
		logAnalyticsWorkspaceId: law.outputs.workspaceId
	}
}

module keyVault 'modules/key-vault.bicep' = {
	name: 'keyvault'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
	}
}

module workloadIdentity 'modules/user-assigned-identity.bicep' = {
	name: 'workload-identity'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
	}
}

module storage 'modules/storage-blob.bicep' = {
	name: 'storage'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
	}
}

@description('Service Bus SKU (Standard default; Basic saves cost for pilots — queues work on Basic).')
@allowed(['Basic', 'Standard'])
param serviceBusSkuName string = 'Standard'

@allowed(['Basic', 'Standard'])
param serviceBusSkuTier string = 'Standard'

module serviceBus 'modules/service-bus.bicep' = {
	name: 'servicebus'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
		skuName: serviceBusSkuName
		skuTier: serviceBusSkuTier
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

module containerRegistry 'modules/container-registry.bicep' = if (deployContainerRegistry) {
	name: 'container-registry'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
		skuName: containerRegistrySkuName
	}
}

module acaEnv 'modules/containerapps-env.bicep' = {
	name: 'containerapps-env'
	params: {
		location: location
		nameSuffix: nameSuffix
		tags: commonTags
		logAnalyticsCustomerId: law.outputs.customerId
		logAnalyticsSharedKey: law.outputs.primarySharedKey
	}
}

module apiApp 'modules/container-app-api.bicep' = if (deployApiContainerApp && !empty(apiContainerImage)) {
	name: 'container-app-api'
	params: {
		location: location
		tags: commonTags
		nameSuffix: nameSuffix
		managedEnvironmentId: acaEnv.outputs.environmentId
		workloadIdentityId: workloadIdentity.outputs.identityId
		containerImage: apiContainerImage
		applicationInsightsConnectionString: apiApplicationInsightsConnectionString
	}
}

module ingestWorkerApp 'modules/container-app-ingest-worker.bicep' = if (deployIngestWorkerContainerApp && !empty(ingestWorkerContainerImage)) {
	name: 'container-app-ingest-worker'
	params: {
		location: location
		tags: commonTags
		nameSuffix: nameSuffix
		managedEnvironmentId: acaEnv.outputs.environmentId
		workloadIdentityId: workloadIdentity.outputs.identityId
		containerImage: ingestWorkerContainerImage
		applicationInsightsConnectionString: apiApplicationInsightsConnectionString
	}
}

module portalApp 'modules/container-app-portal.bicep' = if (deployPortalContainerApp && !empty(portalContainerImage) && !empty(portalApiBaseUrl)) {
	name: 'container-app-portal'
	params: {
		location: location
		tags: commonTags
		nameSuffix: nameSuffix
		managedEnvironmentId: acaEnv.outputs.environmentId
		workloadIdentityId: workloadIdentity.outputs.identityId
		containerImage: portalContainerImage
		apiBaseUrl: portalApiBaseUrl
		defaultDepartmentId: portalDefaultDepartmentId
		defaultClassification: portalDefaultClassification
		applicationInsightsConnectionString: apiApplicationInsightsConnectionString
	}
}

// ---------------------------------------------------------------------------
// RBAC — grant the workload managed identity data-plane access. Built-in role
// ids are stable Azure-wide GUIDs.
// ---------------------------------------------------------------------------

resource keyVaultExisting 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
	name: keyVaultName
}

resource storageExisting 'Microsoft.Storage/storageAccounts@2023-04-01' existing = {
	name: storageAccountName
}

resource serviceBusExisting 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
	name: serviceBusNamespaceName
}

resource containerRegistryExisting 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = if (deployContainerRegistry) {
	name: containerRegistryName
}

var roleKeyVaultSecretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var roleStorageBlobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92ae5b-af97-4fb7-a800-b679082fdda4')
var roleServiceBusDataOwner = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-bcb6-47d8-8c73-093fc9353940')
var roleAcrPull = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource rbacKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
	name: guid(resourceGroup().id, subscription().subscriptionId, keyVaultName, 'kv-secrets-user', nameSuffix)
	scope: keyVaultExisting
	properties: {
		roleDefinitionId: roleKeyVaultSecretsUser
		principalId: workloadIdentity.outputs.principalId
		principalType: 'ServicePrincipal'
	}
	dependsOn: [
		keyVault
	]
}

resource rbacStorageBlobDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
	name: guid(resourceGroup().id, subscription().subscriptionId, storageAccountName, 'st-blob-contrib', nameSuffix)
	scope: storageExisting
	properties: {
		roleDefinitionId: roleStorageBlobDataContributor
		principalId: workloadIdentity.outputs.principalId
		principalType: 'ServicePrincipal'
	}
	dependsOn: [
		storage
	]
}

resource rbacServiceBusDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
	name: guid(resourceGroup().id, subscription().subscriptionId, serviceBusNamespaceName, 'sb-data-owner', nameSuffix)
	scope: serviceBusExisting
	properties: {
		roleDefinitionId: roleServiceBusDataOwner
		principalId: workloadIdentity.outputs.principalId
		principalType: 'ServicePrincipal'
	}
	dependsOn: [
		serviceBus
	]
}

resource rbacAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployContainerRegistry) {
	name: guid(resourceGroup().id, subscription().subscriptionId, containerRegistryName, 'acr-pull', nameSuffix)
	scope: containerRegistryExisting
	properties: {
		roleDefinitionId: roleAcrPull
		principalId: workloadIdentity.outputs.principalId
		principalType: 'ServicePrincipal'
	}
	dependsOn: [
		containerRegistry
	]
}

// ---------------------------------------------------------------------------
// Outputs — wire into GitHub Actions / Azure DevOps variable groups.
// ---------------------------------------------------------------------------

output nameSuffix string = nameSuffix

output logAnalyticsWorkspaceId string = law.outputs.workspaceId
output logAnalyticsWorkspaceName string = law.outputs.workspaceName

@secure()
output applicationInsightsConnectionString string = appInsights.outputs.connectionString

output keyVaultId string = keyVault.outputs.vaultId
output keyVaultUri string = keyVault.outputs.vaultUri
output keyVaultName string = keyVault.outputs.vaultName

output workloadIdentityId string = workloadIdentity.outputs.identityId
output workloadIdentityPrincipalId string = workloadIdentity.outputs.principalId
output workloadIdentityClientId string = workloadIdentity.outputs.clientId

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

output containerAppsEnvironmentId string = acaEnv.outputs.environmentId
output containerAppsEnvironmentName string = acaEnv.outputs.environmentName
output containerAppsDefaultDomain string = acaEnv.outputs.defaultDomain

#disable-next-line BCP318
output containerRegistryName string = deployContainerRegistry ? containerRegistry.outputs.registryName : ''
#disable-next-line BCP318
output containerRegistryLoginServer string = deployContainerRegistry ? containerRegistry.outputs.loginServer : ''

// Conditional module: outputs are only evaluated when the module is deployed.
#disable-next-line BCP318
output apiContainerAppFqdn string = (deployApiContainerApp && !empty(apiContainerImage)) ? apiApp.outputs.fqdn : ''
#disable-next-line BCP318
output apiContainerAppName string = (deployApiContainerApp && !empty(apiContainerImage)) ? apiApp.outputs.appName : ''

#disable-next-line BCP318
output ingestWorkerContainerAppName string = (deployIngestWorkerContainerApp && !empty(ingestWorkerContainerImage)) ? ingestWorkerApp.outputs.appName : ''

#disable-next-line BCP318
output portalContainerAppFqdn string = (deployPortalContainerApp && !empty(portalContainerImage) && !empty(portalApiBaseUrl)) ? portalApp.outputs.fqdn : ''
#disable-next-line BCP318
output portalContainerAppName string = (deployPortalContainerApp && !empty(portalContainerImage) && !empty(portalApiBaseUrl)) ? portalApp.outputs.appName : ''
