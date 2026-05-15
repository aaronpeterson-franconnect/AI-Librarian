// Application Insights — workspace-based component (modern pattern).

@description('Azure region for the component.')
param location string

@description('Stable suffix derived from resourceGroup().id.')
param nameSuffix string

@description('Resource tags.')
param tags object

@description('Full resource id of the Log Analytics workspace backing this component.')
param logAnalyticsWorkspaceId string

var componentName = take('appi-ai-${nameSuffix}', 255)

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
	name: componentName
	location: location
	tags: tags
	kind: 'web'
	properties: {
		Application_Type: 'web'
		Flow_Type: 'Bluefield'
		Request_Source: 'rest'
		WorkspaceResourceId: logAnalyticsWorkspaceId
	}
}

@description('Application Insights resource id.')
output componentId string = appInsights.id

@description('Application Insights connection string (store in Key Vault in production).')
@secure()
output connectionString string = appInsights.properties.ConnectionString

@description('Instrumentation key (legacy; prefer connection string).')
@secure()
output instrumentationKey string = appInsights.properties.InstrumentationKey
