// AiLibrarian.Api — single-revision Container App with external HTTP ingress.



@description('Azure region (must match managed environment).')

param location string



@description('Resource tags.')

param tags object



@description('Stable suffix for naming.')

param nameSuffix string



@description('Managed environment resource id (parent CAE).')

param managedEnvironmentId string



@description('User-assigned managed identity resource id (for Key Vault / data-plane RBAC already assigned in main).')

param workloadIdentityId string



@description('Full container image reference (ACR, GHCR, or other registry reachable by Azure).')

param containerImage string



@secure()

@description('Optional Application Insights connection string; when set, mounted as a secret.')

param applicationInsightsConnectionString string = ''



var appName = take('ca-api-${nameSuffix}', 32)

var hasAppInsights = length(applicationInsightsConnectionString) > 0



resource api 'Microsoft.App/containerApps@2024-03-01' = {

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

			ingress: {

				external: true

				targetPort: 8080

				transport: 'auto'

			}

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

					name: 'api'

					image: containerImage

					env: hasAppInsights

						? [

								{

									name: 'ASPNETCORE_URLS'

									value: 'http://+:8080'

								}

								{

									name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'

									secretRef: 'appinsights-cs'

								}

							]

						: [

								{

									name: 'ASPNETCORE_URLS'

									value: 'http://+:8080'

								}

							]

					resources: {

						cpu: json('0.5')

						memory: '1Gi'

					}

					probes: [

						{

							type: 'Liveness'

							httpGet: {

								path: '/health'

								port: 8080

								scheme: 'HTTP'

							}

							initialDelaySeconds: 15

							periodSeconds: 30

						}

						{

							type: 'Readiness'

							httpGet: {

								path: '/health'

								port: 8080

								scheme: 'HTTP'

							}

							initialDelaySeconds: 5

							periodSeconds: 15

						}

					]

				}

			]

			scale: {

				minReplicas: 1

				maxReplicas: 3

			}

		}

	}

}



@description('Public FQDN for HTTPS (ingress).')

output fqdn string = api.properties.configuration.ingress.fqdn



@description('Container App resource name.')

output appName string = api.name


