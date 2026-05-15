# ADR 0003 — Microsoft Semantic Kernel as the LLM-agnostic gateway

> Status: **Accepted** · Date: 2026-04-29 · Deciders: Architect (initial proposal — to be ratified)

## Context

A non-negotiable requirement is that AI Librarian is **LLM-agnostic**:
no hard dependency on any one model provider, with the ability to
hot-swap providers by configuration. We may run on Azure OpenAI today,
need to switch to Anthropic on Bedrock tomorrow, evaluate a local
deployment on Ollama / vLLM the day after, and want all three to work
without code changes.

We also want a single place where every LLM call is observed:
provider, model, prompt and completion token counts, latency, cost
estimate, error class. That data feeds the audit ledger and per-
department cost dashboards.

## Decision

We adopt **Microsoft Semantic Kernel** as the abstraction layer for
all LLM interactions. We expose three interfaces in
`AiLibrarian.LlmGateway`:

- `IChatProvider` — chat / completion
- `IEmbeddingProvider` — text embedding
- `IRerankProvider` — cross-encoder reranking (optional, Phase 2+)

Each interface is implemented by Semantic Kernel connectors. Provider
selection is by configuration (`appsettings.{env}.json` + Key Vault
secrets):

```jsonc
{
	"LlmGateway": {
		"Chat":      { "Provider": "AzureOpenAI", "Model": "gpt-5.5-medium" },
		"Embedding": { "Provider": "AzureOpenAI", "Model": "text-embedding-3-large" },
		"Rerank":    { "Provider": "Cohere",      "Model": "rerank-english-v3" }
	}
}
```

Connectors wired up in Phase 0:

- Azure OpenAI (primary)
- OpenAI direct (for development convenience)
- Anthropic (via Microsoft.SemanticKernel.Connectors.Anthropic when
  stable, or a thin custom connector)
- Ollama (for local-only environments)

**Provider tier requirements**: per ADR 0012, every configured
provider must be on an enterprise-tier agreement with documented
data-handling commitments (no training, bounded retention, no human
review without consent, tenant-bound data). The gateway enforces this
softly at startup — providers configured without `Tier` and
`DataHandling` metadata generate a warning-level audit event but
proceed. The approved-providers list lives in `docs/llm-providers.md`
and is operator-maintained. See ADR 0012 for the full requirement.

Connectors **disabled by config** (proven by integration tests, not
shipped in production yet):

- AWS Bedrock (Anthropic / other)
- Hugging Face Inference

Per-call interception is implemented as a Semantic Kernel filter that
emits an `audit_events` row containing: caller user/department/role,
purpose ("ingest:editorial-filter", "wiki:maintain", "mcp:ask"),
provider, model, token counts, cost estimate, latency, success/failure.

We **do not** allow direct calls from any service to a provider SDK.
Lint rule (Roslyn analyzer) enforces it: any reference to
`Azure.AI.OpenAI` or `OpenAI` outside `AiLibrarian.LlmGateway` is a
build error.

## Consequences

### Easier

- Hot-swap providers in any environment with a config change.
- One place to apply rate limiting, retry, circuit-breaking.
- One place to attribute cost.
- One place to enforce content-safety policies and prompt guardrails.
- The Synthadoc pattern of using different models for different
  agents (Haiku for lint, Sonnet for synthesis) becomes a config
  decision, not an architecture change.

### Harder

- Semantic Kernel is opinionated; we may occasionally fight its
  abstractions. Mitigation: keep `IChatProvider` etc. small enough that
  we can replace SK with direct HTTP calls if the abstraction breaks
  down.
- New providers may arrive with native features (function calling,
  caching, structured outputs) that the SK connector lags behind on.
  Mitigation: extend the interface or write a custom connector when
  the feature is critical.
- The Roslyn analyzer is moderate effort to maintain.

### Risks

- SK API churn between versions. Mitigation: pin to a stable release;
  upgrade on a deliberate cadence with regression tests.

## Alternatives considered

### LangChain.NET / LlamaIndex.NET

Both exist but have smaller adoption and ecosystem support than
Semantic Kernel inside .NET. Semantic Kernel is first-party Microsoft
and is the path of least resistance on Azure.

### A thin custom abstraction over OpenAI-compatible HTTP APIs

Possible but reinvents the wheel for chat-message formatting, token
counting, structured output coercion, and tool-calling. Justifiable
only if SK proves too restrictive — we keep the gateway abstractions
thin enough to take this path later.

### Direct provider SDKs everywhere

Violates the LLM-agnostic requirement and scatters cost / audit
plumbing across the codebase. Rejected.

## References

- [Microsoft Semantic Kernel docs](https://learn.microsoft.com/en-us/semantic-kernel/)
- [Synthadoc multi-model hot-swap pattern](https://github.com/axoviq-ai/synthadoc)
- ADR 0002 — Stack
- ADR 0010 — Audit ledger
