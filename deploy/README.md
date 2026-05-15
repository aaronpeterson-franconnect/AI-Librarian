# `deploy/` — Infrastructure-as-code

AI Librarian's hyperscaler footprint. Per [ADR 0013](../docs/adr/0013-hyperscaler-deployment-scope.md)
the system is cloud-only. Azure is primary; AWS lives in a separate folder
when we cut the alternate (post-Phase 0).

## Layout

```
deploy/
├── bicep/                        # Azure (primary)
│   ├── main.bicep                # full footprint; resource-group scope
│   ├── main-pilot.bicep          # slim pilot (Postgres + Storage + Service Bus Basic only)
│   ├── modules/
│   │   ├── log-analytics.bicep
│   │   ├── application-insights.bicep
│   │   ├── key-vault.bicep
│   │   ├── user-assigned-identity.bicep
│   │   ├── storage-blob.bicep
│   │   ├── service-bus.bicep
│   │   ├── postgres-flexible.bicep
│   │   ├── containerapps-env.bicep
│   │   └── container-app-api.bicep
│   └── parameters/
│       ├── dev.bicepparam
│       ├── pilot.bicepparam
│       ├── stage.bicepparam
│       └── prod.bicepparam
├── terraform/                    # AWS (alternate, not yet started)
└── runbooks/                     # operational procedures
```

## Conventions

- **Bicep tabs**, two-character width, per `.editorconfig`.
- **Module per resource**. `main.bicep` composes; modules do not compose
  other modules.
- **Parameter files per environment**, never inlined secrets.
- **Names hashed off `resourceGroup().id`** via `uniqueString(resourceGroup().id, deployment().name)` for global uniqueness on Storage / Service Bus / etc.
- **No secrets in source**. PostgreSQL admin password is `@secure()` and must be
  passed at deploy time. Client secrets land in Key Vault after Entra registration
  (see `runbooks/entra-app-registrations.md`).
- **Audit-relevant resources are tagged** with `audit-scope = ai-librarian`
  so the SIEM export filter in [ADR 0010](../docs/adr/0010-audit-ledger.md)
  can pick them up by tag.

## Slim pilot (`main-pilot.bicep`)

For an **initial pilot** when you only need **Postgres + blob storage + Service Bus** and plan to run the **API, portal, worker, and MCP on developer machines** (or your own small compute) using **connection strings**:

1. Create a resource group (for example `rg-ai-librarian-pilot`).
2. Run **what-if** / **create** with `deploy/bicep/main-pilot.bicep` and `deploy/bicep/parameters/pilot.bicepparam` (same `postgresAdminPassword` pattern as below).
3. Copy **connection strings** from the Azure portal (Storage account, Service Bus namespace, PostgreSQL) into user secrets or your pilot secret store — there is **no Key Vault** in this template.
4. Service Bus is deployed as **Basic** (cheaper than Standard; queues are sufficient for ingest in early pilots).

When you need **Application Insights**, **Key Vault**, **managed identity + RBAC**, or **Container Apps**, deploy **`main.bicep`** instead (or add a second deployment to the same group after deleting overlapping names — prefer a fresh RG when graduating).

Use [`runbooks/pilot-minimal-azure.md`](runbooks/pilot-minimal-azure.md) for the full pilot path: deploy, create the database, run Liquibase, seed one Engineering department, pull connection strings, and start the local apps.

```sh
az deployment group create \
	--resource-group rg-ai-librarian-pilot \
	--name ai-librarian-pilot-001 \
	--template-file deploy/bicep/main-pilot.bicep \
	--parameters deploy/bicep/parameters/pilot.bicepparam \
	--parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
```

## Prerequisites

- Azure CLI 2.20+ (`az`)
- Bicep (`az bicep install` if `az bicep version` fails)
- A resource group (create first): `az group create -n rg-ai-librarian-dev -l eastus2`

## What `main.bicep` deploys

| Area | Resources |
|---|---|
| Observability | Log Analytics workspace + workspace-based Application Insights |
| Secrets | Key Vault (RBAC model) |
| Identity | User-assigned managed identity for Container Apps / workers |
| Data plane RBAC | MI → Key Vault Secrets User, Storage Blob Data Contributor, Service Bus Data Owner |
| Storage | StorageV2, blob versioning, `sources` container, immutability policy (no `state` on create — operators lock in prod) |
| Messaging | Service Bus namespace (**Standard** by default; pass `serviceBusSkuName` / `serviceBusSkuTier` = **Basic** to reduce pilot cost) |
| Database | PostgreSQL Flexible Server 16, Burstable dev SKU by default, `azure.extensions = VECTOR`, Azure-services firewall rule |
| Compute host | Container Apps **managed environment** + optional **API** Container App (`deployApiContainerApp`, `apiContainerImage` on `main.bicep`) |

## Deploy (resource group)

```sh
az login
az account set --subscription <subscription-id>

az deployment group what-if \
	--resource-group rg-ai-librarian-dev \
	--template-file deploy/bicep/main.bicep \
	--parameters deploy/bicep/parameters/dev.bicepparam \
	--parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"

az deployment group create \
	--resource-group rg-ai-librarian-dev \
	--name ai-librarian-infra-dev-001 \
	--template-file deploy/bicep/main.bicep \
	--parameters deploy/bicep/parameters/dev.bicepparam \
	--parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
```

Compile only (CI validation):

```sh
az bicep build --file deploy/bicep/main.bicep
```

## Entra ID

App registrations and group-to-role mapping are **not** in Bicep. Follow
[`runbooks/entra-app-registrations.md`](runbooks/entra-app-registrations.md).

## Phase 0 status

Bicep baseline for the Azure footprint is **in place**. The **AiLibrarian.Api**
Container App is **optional** behind `deployApiContainerApp` and a non-empty
`apiContainerImage` (see parameters below). MCP host, ingest worker, private
endpoints, and Entra-only PostgreSQL auth remain **Phase 1+** hardening items.

### Phase 0 smoke checklist (operator)

Per [`docs/phasing.md`](../docs/phasing.md), Phase 0 is complete when a developer
can run infrastructure validation, deploy, sign in, and see an audited LLM
round-trip. In-repo automation covers the **unconfigured** path (`503` from the
smoke route); a configured Azure OpenAI resource is required for a live reply.

1. **Compile Bicep** (CI-friendly): `az bicep build --file deploy/bicep/main.bicep`
2. **What-if** against a dev resource group (set `AILIB_PG_ADMIN_PASSWORD` first):

   ```sh
   az deployment group what-if \
   	--resource-group rg-ai-librarian-dev \
   	--template-file deploy/bicep/main.bicep \
   	--parameters deploy/bicep/parameters/dev.bicepparam \
   	--parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD"
   ```

3. **Deploy** with the same parameters as in [Deploy (resource group)](#deploy-resource-group).

   **Optional — API Container App**: build and push an image, then redeploy with
   extra parameters (public or private registry; private ACR requires AcrPull
   on the workload managed identity and a `registries` block — extend
   `container-app-api.bicep` when you adopt ACR):

   ```sh
   docker build -f src/AiLibrarian.Api/Dockerfile -t <registry>/ailib-api:<tag> .
   docker push <registry>/ailib-api:<tag>

   az deployment group create \
   	--resource-group rg-ai-librarian-dev \
   	--name ai-librarian-infra-dev-002 \
   	--template-file deploy/bicep/main.bicep \
   	--parameters deploy/bicep/parameters/dev.bicepparam \
   	--parameters postgresAdminPassword="$Env:AILIB_PG_ADMIN_PASSWORD" \
   	--parameters deployApiContainerApp=true \
   	--parameters apiContainerImage="<registry>/ailib-api:<tag>" \
   	--parameters apiApplicationInsightsConnectionString="$Env:AILIB_APPINSIGHTS_CS"
   ```

   Deployment outputs include **`apiContainerAppFqdn`** when the API app is
   deployed. Configure the API’s `ConnectionStrings:Postgres`, `AzureAd:*`,
   `LlmGateway:*`, and `IngestQueue:*` via Key Vault references or revision
   env (extend Bicep or use a post-deploy step) before expecting authenticated
   LLM smoke against the public URL.

4. **Entra**: register the API app and grant a test user access per
   [`runbooks/entra-app-registrations.md`](runbooks/entra-app-registrations.md).
5. **API + LLM**: configure `LlmGateway:Providers:azure-openai` (endpoint,
   deployments, `Enabled: true`, tier metadata per ADR 0012) and deploy the API
   with Key Vault / managed identity as appropriate. Call:

   `POST /api/smoke/llm/hello` with a valid Bearer token when Entra is enabled.

   - **200** — body includes `correlationId`, `providerId`, `model`, `reply`; the
     gateway emits audited `llm.chat.stream` metadata (see `AzureOpenAiChatProvider`).
   - **503** — Azure OpenAI is not fully configured; see response `detail` and
     [`docs/llm-providers.md`](../docs/llm-providers.md).
