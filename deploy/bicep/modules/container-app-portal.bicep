// AiLibrarian.Portal — Blazor Server Container App with external HTTP
// ingress. Mirrors the API container app shape (single revision,
// /health probes, App Insights via secret) with two additions: an
// Api:BaseUrl env var pointing at the API container, and Portal-
// specific upload-form defaults driven by parameters so each
// environment can pre-fill the pilot department.

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

@description('Base URL the portal uses to call the API (e.g. https://ca-api-xxxxx.<env>.azurecontainerapps.io/). Required so the portal HttpClient resolves the API surface.')
param apiBaseUrl string

@description('Pre-filled department GUID for the upload form (Phase 1 single-pilot mode). Empty values surface as a 400 on upload; operator sets per-environment.')
param defaultDepartmentId string = ''

@description('Pre-filled classification on the upload form. Defaults to Internal per ADR 0011.')
param defaultClassification string = 'Internal'

@secure()
@description('Optional Application Insights connection string; mounted as a secret when set.')
param applicationInsightsConnectionString string = ''

var appName = take('ca-portal-${nameSuffix}', 32)
var hasAppInsights = length(applicationInsightsConnectionString) > 0

var baseEnv = [
	{
		name: 'ASPNETCORE_URLS'
		value: 'http://+:8080'
	}
	{
		name: 'Api__BaseUrl'
		value: apiBaseUrl
	}
	{
		name: 'Portal__DefaultDepartmentId'
		value: defaultDepartmentId
	}
	{
		name: 'Portal__DefaultClassification'
		value: defaultClassification
	}
]

resource portal 'Microsoft.App/containerApps@2024-03-01' = {
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
					name: 'portal'
					image: containerImage
					env: hasAppInsights
						? concat(baseEnv, [
								{
									name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
									secretRef: 'appinsights-cs'
								}
							])
						: baseEnv
					resources: {
						cpu: json('0.5')
						memory: '1Gi'
					}
					probes: [
						{
							type: 'Liveness'
							httpGet: {
								// Blazor Server doesn't ship a /health by default; root path is the cheapest reliable signal.
								path: '/'
								port: 8080
								scheme: 'HTTP'
							}
							initialDelaySeconds: 15
							periodSeconds: 30
						}
						{
							type: 'Readiness'
							httpGet: {
								path: '/'
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
output fqdn string = portal.properties.configuration.ingress.fqdn

@description('Container App resource name.')
output appName string = portal.name
