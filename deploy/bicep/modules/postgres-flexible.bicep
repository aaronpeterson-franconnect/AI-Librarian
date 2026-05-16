// Azure Database for PostgreSQL Flexible Server + pgvector extension flag.
// Dev template uses public network access; production should use private
// endpoints + Entra-only auth (follow-up changeSet).

@description('Azure region for the server.')
param location string

@description('Globally unique server name (lowercase letters, numbers, hyphens; 3-63).')
param serverName string

@description('Administrator login (not azure_superuser).')
param administratorLogin string

@description('Administrator password (pass via CLI or secure parameter file; never commit).')
@secure()
param administratorLoginPassword string

@description('Resource tags.')
param tags object

@description('PostgreSQL major version.')
param postgresVersion string = '16'

@description('Burstable SKU name for cost-conscious dev; scale up for production.')
param skuName string = 'Standard_B1ms'

@description('Burstable tier name.')
param skuTier string = 'Burstable'

@description('Name of the application database to create on the server. The Liquibase changelog applies its schema against this database; pilots used to require a separate `az postgres flexible-server db create` call -- this parameter folds that into the same deploy.')
param applicationDatabaseName string = 'ailibrarian'

resource flexibleServer 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
	name: serverName
	location: location
	tags: tags
	sku: {
		name: skuName
		tier: skuTier
	}
	properties: {
		version: postgresVersion
		administratorLogin: administratorLogin
		administratorLoginPassword: administratorLoginPassword
		storage: {
			storageSizeGB: 32
		}
		network: {
			publicNetworkAccess: 'Enabled'
		}
		authConfig: {
			activeDirectoryAuth: 'Disabled'
			passwordAuth: 'Enabled'
		}
		backup: {
			backupRetentionDays: 7
			geoRedundantBackup: 'Disabled'
		}
		highAvailability: {
			mode: 'Disabled'
		}
	}
}

// Allow-list every extension the Liquibase changelog installs in
// 0000-extensions.sql. Azure Database for PostgreSQL gates extensions
// per-server via this `azure.extensions` parameter; anything not listed
// here fails CREATE EXTENSION with "not allow-listed for users".
//
//   VECTOR    : pgvector embeddings (ADR 0001)
//   PG_TRGM   : trigram lexical match in hybrid retrieval
//   CITEXT    : case-insensitive identifiers
//   PGCRYPTO  : gen_random_uuid() and friends
resource pgExtensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
	parent: flexibleServer
	name: 'azure.extensions'
	properties: {
		value: 'VECTOR,PG_TRGM,CITEXT,PGCRYPTO'
		source: 'user-override'
	}
}

// Allow Azure services (Container Apps, etc.) to reach the server — dev convenience.
resource allowAzureServicesFirewall 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
	parent: flexibleServer
	name: 'AllowAllAzureServicesAndResourcesWithinAzureIps_2024-04-01'
	properties: {
		startIpAddress: '0.0.0.0'
		endIpAddress: '0.0.0.0'
	}
}

// Application database. The Liquibase migration runner (Phase 0 runbook
// step 4) targets this database explicitly via `dbname=<name>` in the
// JDBC URL. Without this resource, the server provisions empty and the
// pilot operator must `az postgres flexible-server db create` between
// Bicep and Liquibase -- a discovered-mid-deploy gotcha during the first
// cloud-hosted pilot walkthrough (2026-05-15). Folding it in here keeps
// the runbook to one Bicep step.
resource applicationDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
	parent: flexibleServer
	name: applicationDatabaseName
	properties: {
		// Postgres defaults via the template's collation; explicit values
		// match what `CREATE DATABASE` would pick on a fresh Flexible
		// Server. Specifying them here documents the assumption and
		// avoids drift if Microsoft changes the server defaults.
		charset: 'UTF8'
		collation: 'en_US.utf8'
	}
}

@description('Flexible server resource id.')
output serverId string = flexibleServer.id

@description('Fully qualified domain name of the PostgreSQL server.')
output fqdn string = flexibleServer.properties.fullyQualifiedDomainName

@description('Echo of the administrator login passed at deploy time.')
output administratorLoginOut string = administratorLogin

@description('Name of the application database created on the server.')
output applicationDatabaseNameOut string = applicationDatabase.name
