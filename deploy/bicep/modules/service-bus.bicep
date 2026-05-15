// Azure Service Bus namespace — async ingest / wiki maintenance / RTBF jobs.

@description('Azure region for the namespace.')
param location string

@description('Stable suffix derived from resourceGroup().id.')
param nameSuffix string

@description('Resource tags.')
param tags object

@description('Service Bus SKU name. Basic is lowest cost and supports queues needed for ingest.')
@allowed(['Basic', 'Standard', 'Premium'])
param skuName string = 'Standard'

@description('Service Bus SKU tier (must match skuName: Basic/Basic, Standard/Standard, Premium/Premium).')
@allowed(['Basic', 'Standard', 'Premium'])
param skuTier string = 'Standard'

@description('Ingest queue consumed by AiLibrarian.IngestWorker and written by AiLibrarian.Api.')
param ingestQueueName string = 'ingest-jobs'

var namespaceName = take('sb-ai-${nameSuffix}', 50)

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
	name: namespaceName
	location: location
	tags: tags
	sku: {
		name: skuName
		tier: skuTier
	}
	properties: {
		minimumTlsVersion: '1.2'
		publicNetworkAccess: 'Enabled'
		disableLocalAuth: false
		zoneRedundant: false
	}
}

resource ingestQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
	name: ingestQueueName
	parent: serviceBusNamespace
	properties: {
		maxSizeInMegabytes: 1024
		defaultMessageTimeToLive: 'P14D'
		lockDuration: 'PT1M'
		maxDeliveryCount: 10
	}
}

@description('Service Bus namespace resource id.')
output namespaceId string = serviceBusNamespace.id

@description('Service Bus hostname (for connection strings / RBAC).')
output hostname string = '${serviceBusNamespace.name}.servicebus.windows.net'

@description('Service Bus namespace name.')
output namespaceName string = serviceBusNamespace.name

@description('Ingest queue name.')
output ingestQueueName string = ingestQueue.name
