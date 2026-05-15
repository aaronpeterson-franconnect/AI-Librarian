// User-assigned managed identity — attached to Container Apps / jobs for
// Key Vault, Storage, Service Bus, and (later) Azure OpenAI without
// embedding secrets in source control.

@description('Azure region for the identity.')
param location string

@description('Stable suffix derived from resourceGroup().id.')
param nameSuffix string

@description('Resource tags.')
param tags object

var identityName = take('id-aca-${nameSuffix}', 128)

resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
	name: identityName
	location: location
	tags: tags
}

@description('User-assigned identity resource id.')
output identityId string = managedIdentity.id

@description('Principal id of the managed identity (for RBAC role assignments).')
output principalId string = managedIdentity.properties.principalId

@description('Client id of the managed identity (for Entra token acquisition from workloads).')
output clientId string = managedIdentity.properties.clientId
