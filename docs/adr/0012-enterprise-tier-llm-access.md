# ADR 0012 — All LLM access requires enterprise-tier provider agreements

> Status: **Accepted** · Date: 2026-04-30 · Deciders: Architect

## Context

Every LLM call in AI Librarian routes corporate knowledge through a
model provider's API or a local inference engine:

- The **server-side LLM Gateway** (ADR 0003) makes calls for ingestion
  (embedding, classification, summarization), wiki maintenance
  (synthesis, claim extraction, reranking), and user `ask` queries.
- The **client-side AI clients** (Cursor, Copilot, Claude Desktop,
  ChatGPT, Teams) make their own LLM calls when interacting with the
  MCP server (ADR 0004) — using whatever provider that AI tool is
  configured with.

By default, many provider plans (consumer ChatGPT, free-tier Claude,
public OpenAI API tier, free Cursor accounts, etc.) reserve the right
to:

- Use customer inputs for model training
- Retain content beyond the duration of the request
- Have human reviewers examine inputs and outputs
- Comingle data across customers

None of those data-handling postures are acceptable for an enterprise
knowledge platform that ingests proprietary code, internal financials,
HR records, and customer information.

**The architecture's privacy posture rests on a contractual
guarantee** that every LLM endpoint AI Librarian touches operates
under an enterprise-tier agreement with documented data-handling
commitments. Without that guarantee, classification labels, audit
metadata-only policy, and tiered deletion are all undermined: the
provider could retain, train on, or expose data the system thought
was protected.

## Decision

### The principle

**Every LLM endpoint AI Librarian sends data to must be operating
under an enterprise-tier agreement that contractually guarantees**:

1. **No training** on customer data
2. **Bounded data retention** (zero-day or short fixed window for
   abuse monitoring; ideally Zero Data Retention where available)
3. **No human review** of inputs/outputs without explicit consent
4. **Tenant-bound or customer-bound data** — content does not flow
   to other customers
5. **Compliance with the company's existing Data Processing
   Agreements**

This applies to **both** the server-side LLM Gateway and the
client-side AI clients accessing via MCP.

### Server-side enforcement (LLM Gateway)

The LLM Gateway treats provider tier as a first-class configuration
concern. Each configured provider in `appsettings.{env}.json` carries
**tier metadata** documenting the approved tier and the data-handling
guarantee it provides:

```jsonc
{
	"LlmGateway": {
		"Chat": {
			"Provider": "AzureOpenAI",
			"Tier": "EnterpriseTenantStandard",
			"DataHandling": "no-training; abuse-monitoring opt-out filed",
			"Model": "gpt-5.5-medium"
		},
		"Embedding": {
			"Provider": "AzureOpenAI",
			"Tier": "EnterpriseTenantStandard",
			"DataHandling": "no-training; abuse-monitoring opt-out filed",
			"Model": "text-embedding-3-large"
		}
	}
}
```

At startup, the gateway:

1. Reads the tier metadata for each configured provider
2. Emits an `audit.startup.providers_configured` event listing the
   `(provider, tier, model, data_handling)` tuples
3. **If `Tier` or `DataHandling` is missing or empty** for a
   configured provider, the gateway emits a **WARNING-level audit
   event** `audit.startup.provider_tier_unverified` and proceeds.
   The gateway does *not* block startup. The warning surfaces in
   monitoring; it's the operator's responsibility to fix.

This is deliberately soft enforcement: while we build out the system,
we want flexibility to evaluate providers without checkpointing every
config change against a hard allow-list. Operational hygiene (review
the warnings; complete the tier metadata; periodically audit
configured providers against current vendor agreements) carries the
weight that hard enforcement would.

The approved-providers allow-list lives in `docs/llm-providers.md`
as a **configuration-tracked living document**, not embedded in code.
Ops and Security keep it current; new providers are added by editing
that document and the corresponding `appsettings.{env}.json` —
**no ADR amendment required for routine additions**, on the
understanding that the operator confirmed the tier and data-handling
posture before adding the entry.

### Client-side documented requirement (AI clients via MCP)

The MCP layer cannot enforce a third-party AI client's licensing
arrangement at the protocol level. Instead:

1. The IT runbook (Phase 0 deliverable) documents the **approved
   AI client tiers** — Copilot (M365 Enterprise/Business), GitHub
   Copilot (Business/Enterprise), Cursor (Business/Enterprise with
   Privacy Mode), Claude (Work/Team/Enterprise), ChatGPT
   (Enterprise/Business with training opt-out), Teams (M365
   Enterprise). The list grows with the AI tool market.

2. The workstation `AiLibrarian.Cli` (ADR 0004) records the
   connecting client's identity (user-agent, process name) on every
   session. The audit ledger therefore shows which AI clients are
   touching corporate data.

3. The CLI emits a **non-blocking warning** to the user if it detects
   a client identity outside the approved list (e.g., consumer
   ChatGPT Desktop). The warning is informational; the connection
   proceeds.

4. Security periodically reviews the audit ledger for client-identity
   anomalies. Patterns of non-approved clients trigger a remediation
   conversation with the affected user.

### Audit alignment

This ADR aligns with ADR 0010's content-capture policy: LLM-call
audit events are metadata-only (no prompt or completion content). The
combination is intentional — neither the provider nor the audit
ledger retains the sensitive content of any individual conversation.
Forensic investigation requires either the user's cooperation or
opt-in diagnostic content capture (per ADR 0010).

### Operational checks

The following are recurring obligations rather than v1 deliverables,
captured here so they're not forgotten:

- **Quarterly provider review**: Ops + Security review every
  configured provider in production against the current vendor DPA;
  any change in vendor terms triggers a re-evaluation.
- **Subscribe to provider changelogs**: at least one Security team
  member subscribes to each approved provider's data-handling
  policy changelog.
- **Annual contract refresh**: alignment with the company's master
  data processing agreements happens annually as part of the standard
  vendor management cycle.

## Consequences

### Easier

- Privacy posture is contractual, not best-effort.
- Operators have a clear configuration shape (`Tier` +
  `DataHandling`) that prompts the right questions at the right time.
- Audit forensics show which AI clients touched data.
- Aligns with the existing Azure-tenant data handling story.
- The soft-enforcement choice keeps the build-out unblocked while
  still surfacing missing tier metadata as warnings.

### Harder

- Operators must fill in tier metadata when configuring a new
  provider — a small but real friction point.
- The approved-list document (`docs/llm-providers.md`) must be kept
  current; if it goes stale, warnings stop being meaningful.
- The CLI's client-identity check is heuristic (process name /
  user-agent strings can be spoofed). Defense-in-depth is the audit
  ledger and periodic Security review, not the warning itself.

### Risks

- A provider's data-handling terms change after we approved it; we
  discover out of band. **Mitigation**: quarterly review; subscribe
  to provider changelogs.
- Soft enforcement is ignored — operators dismiss the warning rather
  than filling in tier metadata. **Mitigation**: monitoring alerts on
  `audit.startup.provider_tier_unverified` events; security includes
  this in the quarterly review.
- A user uses a non-approved AI client and we detect only after the
  fact via audit. **Mitigation**: the CLI warning at first connection
  is the proactive cue; remediation is conversation-not-block.
- Local-only model fallback (Ollama / vLLM) bypasses provider data
  concerns but introduces different risks (slower, lower-quality
  models). **Acceptable** as a backup option; not the default.

## Alternatives considered

### Hard enforcement (refuse to initialize without verified tier)

Considered. Rejected for v1 because we want flexibility to evaluate
providers during build-out. Hard enforcement is a sensible Phase 4+
hardening when the approved-providers list is stable. Captured in
[`../future-enhancements.md`](../future-enhancements.md) as an
operational hardening item.

### Trust-based with no enforcement at all

Considered and rejected. The audit-event-on-startup is the minimum
visibility we need; warnings on missing tier metadata are the minimum
nudge.

### Always-local-only (no cloud LLMs)

Strongest privacy posture; sacrifices state-of-the-art quality.
Available as a configuration option for departments with the
strictest needs but rejected as the default.

### Per-department provider opt-in

Possible later (HR uses local; Engineering uses Azure OpenAI). Adds
complexity. Captured in `future-enhancements.md` as item AC-P4
(provisional).

## References

- ADR 0003 — LLM Gateway (Semantic Kernel)
- ADR 0004 — MCP as single access layer (covers CLI + client identity)
- ADR 0010 — Audit ledger (metadata-only LLM events)
- ADR 0011 — Data classification (the labels this ADR protects)
- [Azure OpenAI Service data, privacy, and security](https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/data-privacy)
- [Anthropic enterprise / ZDR overview](https://www.anthropic.com/legal/aup)
- [OpenAI enterprise data handling](https://openai.com/enterprise-privacy/)
- `docs/llm-providers.md` — living approved-providers / approved-clients list (created in Phase 0)
