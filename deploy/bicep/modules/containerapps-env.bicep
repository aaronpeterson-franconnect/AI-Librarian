// Azure Container Apps managed environment — hosts API, MCP, workers, jobs.

@description('Azure region for the environment.')
param location string

@description('Stable suffix derived from resourceGroup().id.')
param nameSuffix string

@description('Resource tags.')
param tags object

@description('Log Analytics customer id (workspace customerId).')
param logAnalyticsCustomerId string

@description('Log Analytics primary shared key (secure).')
@secure()
param logAnalyticsSharedKey string

var environmentName = take('cae-ai-${nameSuffix}', 60)

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
	name: environmentName
	location: location
	tags: tags
	properties: {
		appLogsConfiguration: {
			destination: 'log-analytics'
			logAnalyticsConfiguration: {
				customerId: logAnalyticsCustomerId
				sharedKey: logAnalyticsSharedKey
			}
		}
	}
}

@description('Managed environment resource id.')
output environmentId string = containerAppsEnvironment.id

@description('Managed environment name.')
output environmentName string = containerAppsEnvironment.name

@description('Default domain suffix for apps in this environment (*.REGION.azurecontainerapps.io).')
output defaultDomain string = containerAppsEnvironment.properties.defaultDomain
