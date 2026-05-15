// Log Analytics workspace — central sink for Container Apps + App Insights.
// Per deploy/README.md: tag with audit-scope = ai-librarian.

@description('Azure region for the workspace.')
param location string

@description('Stable suffix derived from resourceGroup().id (globally unique fragment).')
param nameSuffix string

@description('Resource tags applied to every child resource.')
param tags object

var workspaceName = take('law-ai-${nameSuffix}', 63)

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
	name: workspaceName
	location: location
	tags: tags
	properties: {
		sku: {
			name: 'PerGB2018'
		}
		retentionInDays: 30
		features: {
			enableLogAccessUsingOnlyResourcePermissions: true
		}
	}
}

@description('Resource id of the Log Analytics workspace.')
output workspaceId string = workspace.id

@description('Workspace resource name (for existing + listKeys in parent template).')
output workspaceName string = workspace.name

@description('Log Analytics customer id (workspace id GUID) for ACA log ingestion.')
output customerId string = workspace.properties.customerId

@description('Primary shared key for ACA managedEnvironment logAnalyticsConfiguration.')
@secure()
output primarySharedKey string = workspace.listKeys().primarySharedKey
