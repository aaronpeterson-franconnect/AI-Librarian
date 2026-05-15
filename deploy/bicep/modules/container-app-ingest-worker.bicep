// AiLibrarian.IngestWorker — Service Bus-driven Container App. No ingress
// because the worker is consumer-only; it pulls messages off the
// IngestQueue and writes to Postgres + Blob via the workload identity.
//
// Phase 1 keeps the long-running BackgroundService consumer pattern from
// IngestServiceBusHostedService; KEDA-based event scaling becomes a
// later refinement once we have real traffic numbers to size against.

@description('Azure region (must match managed environment).')
param location string

@description('Resource tags.')
param tags object

@description('Stable suffix for naming.')
param nameSuffix string

@description('Managed environment resource id (parent CAE).')
param managedEnvironmentId string

@description('User-assigned managed identity resource id (RBAC already assigned in main).')
param workloadIdentityId string

@description('Full container image reference (ACR, GHCR, or other registry reachable by Azure).')
param containerImage string

@secure()
@description('Optional Application Insights connection string; mounted as a secret when set.')
param applicationInsightsConnectionString string = ''

@description('Minimum replicas. Workers stay warm so Service Bus messages process without cold-start lag; size up for higher throughput.')
@minValue(0)
@maxValue(10)
param minReplicas int = 1

@description('Maximum replicas. Phase 1 default is small; raise once a real eval shows queue lag.')
@minValue(1)
@maxValue(30)
param maxReplicas int = 3

var appName = take('ca-ingest-${nameSuffix}', 32)
var hasAppInsights = length(applicationInsightsConnectionString) > 0

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
	name: appName
	location: location
	tags: tags
	identity: {
		type: 'UserAssigned'
		userAssignedIdentities: {
			'${workloadIdentityId}': {}
		}
	}
	properties: {
		managedEnvironmentId: managedEnvironmentId
		configuration: {
			activeRevisionsMode: 'Single'
			// No `ingress` block — the worker is consumer-only.
			secrets: hasAppInsights
				? [
						{
							name: 'appinsights-cs'
							value: applicationInsightsConnectionString
						}
					]
				: []
		}
		template: {
			containers: [
				{
					name: 'ingest-worker'
					image: containerImage
					env: hasAppInsights
						? [
								{
									name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
									secretRef: 'appinsights-cs'
								}
							]
						: []
					resources: {
						cpu: json('0.5')
						memory: '1Gi'
					}
					// No HTTP probes — no ingress. The worker uses Service Bus
					// processor lifecycle as its readiness signal; Container
					// Apps platform monitors the container process itself.
				}
			]
			scale: {
				minReplicas: minReplicas
				maxReplicas: maxReplicas
			}
		}
	}
}

@description('Container App resource name.')
output appName string = worker.name
