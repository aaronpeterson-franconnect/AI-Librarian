# Minimal Azure pilot runbook

This path keeps Azure small for the first pilot:

- Azure hosts only **PostgreSQL**, **Blob Storage**, and **Service Bus Basic**.
- The **API**, **Portal**, **Ingest worker**, **MCP**, and **CLI** run locally.
- Secrets stay in local environment variables or your pilot secret store; **do not commit them**.

Graduate to `deploy/bicep/main.bicep` when you need Container Apps, Key Vault, managed identity, Application Insights, or hosted API/worker.

## 1. Create the pilot resource group

```powershell
az login
az account set --subscription "<subscription-id>"
az group create -n rg-ai-librarian-pilot -l eastus2
```

## 2. Deploy the slim data plane

Set a strong PostgreSQL admin password in your shell, then run what-if and deploy.

```powershell
$Env:AILIB_PG_ADMIN_PASSWORD = "<strong-password>"

az deployment group what-if `
	--resource-group rg-ai-librarian-pilot `
	--template-file deploy/bicep/main-pilot.bicep `
	--parameters deploy/bicep/parameters/pilot.bicepparam `
	--parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"

az deployment group create `
	--resource-group rg-ai-librarian-pilot `
	--name ai-librarian-pilot-001 `
	--template-file deploy/bicep/main-pilot.bicep `
	--parameters deploy/bicep/parameters/pilot.bicepparam `
	--parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
```

Save the deployment outputs, especially:

- `postgresFqdn`
- `storageAccountName`
- `serviceBusNamespaceName`
- `serviceBusIngestQueueName` (`ingest-jobs`)

## 3. Create the application database

Create a database named `ailibrarian`. Use `psql`, Azure Data Studio, or the Azure Portal query tool.

```powershell
psql "host=<postgres-fqdn> port=5432 dbname=postgres user=ailibrarian password=$Env:AILIB_PG_ADMIN_PASSWORD sslmode=require" `
	-c "CREATE DATABASE ailibrarian;"
```

## 4. Run Liquibase migrations

The migrations create extensions, corpus tables, RLS policies, audit tables, personas, source chunks, and hybrid-search columns.

```powershell
docker run --rm `
	-v "${PWD}/db/changelog:/liquibase/changelog" `
	liquibase/liquibase:4.29 `
	--url="jdbc:postgresql://<postgres-fqdn>:5432/ailibrarian?sslmode=require" `
	--username="ailibrarian" `
	--password="$Env:AILIB_PG_ADMIN_PASSWORD" `
	--changeLogFile="changelog/master.xml" `
	update
```

## 5. Seed one Engineering pilot department

Use fixed IDs during the pilot so local config is stable. This mirrors the defaults in `deploy/pilot.local.env.template`.

```sql
SET app.is_authenticated = 'true';
SET app.is_employee = 'false';
SET app.is_admin = 'true';
SET app.user_id = '00000000-0000-0000-0000-00000000ffff';

INSERT INTO departments (id, name, display_name)
VALUES (
	'11111111-1111-1111-1111-111111111111',
	'engineering',
	'Engineering'
) ON CONFLICT (name) DO NOTHING;

INSERT INTO users (id, email, display_name, is_employee)
VALUES (
	'22222222-2222-2222-2222-222222222222',
	'pilot.engineer@example.com',
	'Pilot Engineer',
	true
) ON CONFLICT (id) DO NOTHING;

INSERT INTO user_authorizations (user_id, department_id, role, source_group_id)
VALUES (
	'22222222-2222-2222-2222-222222222222',
	'11111111-1111-1111-1111-111111111111',
	'Contributor',
	'pilot-local'
) ON CONFLICT DO NOTHING;
```

## 6. Pull connection strings

```powershell
$storage = az deployment group show `
	-g rg-ai-librarian-pilot `
	-n ai-librarian-pilot-001 `
	--query "properties.outputs.storageAccountName.value" -o tsv

$serviceBus = az deployment group show `
	-g rg-ai-librarian-pilot `
	-n ai-librarian-pilot-001 `
	--query "properties.outputs.serviceBusNamespaceName.value" -o tsv

az storage account show-connection-string `
	-g rg-ai-librarian-pilot `
	-n $storage `
	--query connectionString -o tsv

az servicebus namespace authorization-rule keys list `
	-g rg-ai-librarian-pilot `
	--namespace-name $serviceBus `
	--name RootManageSharedAccessKey `
	--query primaryConnectionString -o tsv
```

Copy `deploy/pilot.local.env.template` to `deploy/pilot.local.env`, then replace placeholders with the values above.

## 7. Load local environment variables

```powershell
Get-Content deploy/pilot.local.env |
	Where-Object { $_ -and -not $_.StartsWith("#") } |
	ForEach-Object {
		$name, $value = $_.Split("=", 2)
		Set-Item "env:$name" $value
	}
```

## 8. Run the local apps

Use separate terminals:

```powershell
dotnet run --project src/AiLibrarian.Api
```

```powershell
dotnet run --project src/AiLibrarian.Portal
```

```powershell
dotnet run --project src/AiLibrarian.IngestWorker
```

For the MCP bridge:

```powershell
dotnet run --project src/AiLibrarian.Mcp
```

## 9. Pilot smoke

1. Open the portal at `http://localhost:5215`.
2. Upload a Markdown / DOCX / XLSX / PPTX source.
3. In the upload request, use:
	- `departmentId`: `11111111-1111-1111-1111-111111111111`
	- `contributorId`: `22222222-2222-2222-2222-222222222222`
	- `classification`: `Internal`
4. Enqueue the returned `blobUri` and `sourceId` through `POST /api/ingest/enqueue` or MCP `enqueue_source`.
5. Watch the ingest worker logs for queue processing and chunk persistence.

## 10. Optional LLM / embeddings

After Azure OpenAI is approved for the pilot:

- Set `LlmGateway__Providers__azure-openai__Enabled=true`.
- Set endpoint, API key, chat deployment, and embedding deployment.
- Set `Search__EmbeddingDeployment`.
- Set `IngestWorker__Processing__GenerateEmbeddings=true`.
- Set `IngestWorker__Embeddings__ModelDeploymentName`.

Keep a separate Azure OpenAI budget alert; token / embedding usage is the main variable cost.

## 11. Tear down

For a disposable pilot:

```powershell
az group delete -n rg-ai-librarian-pilot --yes
```
