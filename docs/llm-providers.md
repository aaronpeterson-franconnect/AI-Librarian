# Approved LLM providers and AI clients

This document is the **configuration-tracked living allow-list** for LLM
endpoints and AI tooling that may touch AI Librarian data. It implements the
operational half of [ADR 0012](adr/0012-enterprise-tier-llm-access.md): **every
LLM endpoint must operate under enterprise-tier data-handling guarantees**
(no training on customer data, bounded retention, no human review without
consent, tenant-bound processing).

Routine additions (a new Azure region deployment, a new Anthropic enterprise
endpoint, an additional approved Copilot mode) **do not require a new ADR** —
they require (1) an entry here, (2) matching `LlmGateway:Providers:*` metadata
in `appsettings.{Environment}.json`, and (3) Security / Legal confirmation that
the contractual posture matches the row in this table.

## Server-side providers (LLM Gateway)

These providers are implemented or planned in `AiLibrarian.LlmGateway` and
selected via `LlmGateway:DefaultChatProvider` / `DefaultEmbeddingProvider` in
configuration.

| Provider id | Status | Enterprise posture (summary) | Configuration keys |
|---|---|---|---|
| `azure-openai` | **Implemented** (Semantic Kernel) | Azure OpenAI Service under Microsoft enterprise product terms; customer data not used to train foundation models; abuse monitoring with documented controls. | `Endpoint`, `ChatDeployment`, `EmbeddingDeployment`, optional `ApiKey` (omit for `DefaultAzureCredential`). |
| `anthropic` | **Stub** (disabled by default) | Requires Anthropic **Enterprise** agreement with zero-retention / no-training commitments filed with Legal. | Not wired in code yet — keep `Enabled: false` until the connector ships. |
| `ollama` | **Stub** (disabled by default) | Self-hosted inside our tenant counts as **SelfHosted** tier per ADR 0012 when operated on approved infrastructure. | Not wired in code yet — keep `Enabled: false` until the connector ships. |

### Azure OpenAI checklist

1. Create an Azure OpenAI resource in an approved region.
2. Deploy **chat** and **embedding** models; note the **deployment names**
	(distinct from model names like `gpt-4o`).
3. Grant the workload identity (or developer identity) **Cognitive Services
	OpenAI User** on the resource.
4. Set `LlmGateway:Providers:azure-openai:Enabled` = `true`.
5. Fill `Endpoint` (resource URL, no trailing slash required), `ChatDeployment`,
	and `EmbeddingDeployment`.
6. Prefer **keyless** auth: leave `ApiKey` empty so the gateway uses
	`DefaultAzureCredential` (managed identity in Azure, Azure CLI locally).
7. Restart the API and confirm **no** `audit.startup.provider_tier_unverified`
	warnings in the audit stream / logs.

## Client-side AI tools (MCP consumers)

The MCP server cannot enforce a third-party tool’s license. IT / Security
maintains this list; only tools that meet the ADR 0012 bar may be used against
production MCP endpoints.

| Tool | Approved modes | Notes |
|---|---|---|
| **GitHub Copilot** | Business / Enterprise | Org policy: training opt-out; no public code matching for proprietary repos. |
| **Microsoft Copilot (M365)** | Enterprise / Business | Tenant-bound; governed by Microsoft 365 agreements. |
| **Cursor** | Business / Enterprise with **Privacy Mode** | Must not send code to models that lack enterprise data-handling. |
| **Claude (Anthropic)** | **Work / Team / Enterprise** only | Consumer Claude is **not** approved for corporate AI Librarian data. |
| **ChatGPT** | **Enterprise / Business** with training disabled | Consumer / Plus tiers are **not** approved. |
| **Microsoft Teams** | M365 Enterprise | When used as the host for approved Copilot features. |

Update this table when Legal / IT approves additional tools.

## Operational reminders

- **Warnings, not hard blocks:** misconfigured tier metadata emits
	`audit.startup.provider_tier_unverified` and **does not** stop the process
	(ADR 0012). Treat warnings as Sev-2 until cleared.
- **No prompt/completion capture:** the audit ledger stores **metadata only**
	([ADR 0010](adr/0010-audit-ledger.md)) — token counts, latency, model id,
	correlation id, persona id — never raw prompts or completions.
- **Hyperscaler scope:** deployments target **Azure or AWS** only
	([ADR 0013](adr/0013-hyperscaler-deployment-scope.md)); sovereign / on-prem
	inference is out of scope for this repository.

## Change control

| Change | Owner | Evidence |
|---|---|---|
| Add / remove server provider | Security + Platform | PR updates this file + `appsettings` + Bicep/KeyVault wiring. |
| Add / remove approved AI client | IT / Security | PR updates this table; link internal policy ticket. |
| Relax enterprise requirement | **Not allowed** without a **new ADR** | ADR 0012 is a constitutional constraint. |
