# Bicep modules

One module per Azure resource family. `main.bicep` composes them; modules do not
nest other modules (per `deploy/README.md`).

| Module | Resource |
|---|---|
| `log-analytics.bicep` | Log Analytics workspace |
| `application-insights.bicep` | Workspace-based Application Insights |
| `key-vault.bicep` | Key Vault (RBAC authorization model) |
| `user-assigned-identity.bicep` | User-assigned managed identity for workloads |
| `storage-blob.bicep` | StorageV2 + blob versioning + `sources` container + immutability policy |
| `service-bus.bicep` | Service Bus namespace (SKU via `skuName` / `skuTier`; **Basic** for `main-pilot`, **Standard** default in `main.bicep`) |
| `postgres-flexible.bicep` | PostgreSQL Flexible Server + `azure.extensions = VECTOR` + Azure-services firewall rule |
| `containerapps-env.bicep` | Container Apps managed environment (Log Analytics ingestion) |
| `container-app-api.bicep` | **AiLibrarian.Api** Container App (optional; enabled when `deployApiContainerApp` + `apiContainerImage` are set on `main.bicep`) |
| `container-app-ingest-worker.bicep` | **AiLibrarian.IngestWorker** Container App — Service Bus consumer, no ingress (optional; enabled when `deployIngestWorkerContainerApp` + `ingestWorkerContainerImage` are set) |
| `container-app-portal.bicep` | **AiLibrarian.Portal** Container App — Blazor Server with external HTTP ingress (optional; enabled when `deployPortalContainerApp` + `portalContainerImage` + `portalApiBaseUrl` are set) |

Entra app registrations are documented in `deploy/runbooks/entra-app-registrations.md`
(ARM has no first-class app registration resource).

Note: the MCP server is **stdio-only** in Phase 1 (runs as a child process spawned
by `ailib mcp` on the user's workstation), so it has no Container App module.
A Phase 2 HTTP-transport MCP server would gain its own module then.
