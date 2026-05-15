// Azure Container Registry — pilot footprint. Basic SKU is the
// cheapest tier that still supports az acr login + push; Standard or
// Premium are required for geo-replication and private endpoints,
// neither of which is in scope for the pilot.
//
// AcrPull RBAC for the workload identity is granted in the parent
// bicep so role assignments stay alongside the workload they enable.

@description('Azure region for the registry.')
param location string

@description('Resource tags.')
param tags object

@description('Stable suffix for naming.')
param nameSuffix string

@allowed(['Basic', 'Standard', 'Premium'])
@description('Registry SKU. Pilot default Basic; raise to Standard for higher throughput, Premium for geo-replication / private endpoints.')
param skuName string = 'Basic'

// Registry name: 5-50 alphanumeric, globally unique.
var registryName = take(toLower('acrailib${nameSuffix}'), 50)

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
	name: registryName
	location: location
	tags: tags
	sku: {
		name: skuName
	}
	properties: {
		adminUserEnabled: false
		publicNetworkAccess: 'Enabled'
		// Anonymous pull is off by default at this API version; AcrPull
		// RBAC on the workload identity is the only push/pull path.
	}
}

@description('Registry resource id (for scope on RBAC assignments).')
output registryId string = registry.id

@description('Registry name.')
output registryName string = registry.name

@description('Login server hostname (e.g. acrailibxxxx.azurecr.io). Image tags are <loginServer>/<repo>:<tag>.')
output loginServer string = registry.properties.loginServer
