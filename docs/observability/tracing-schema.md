# Tracing schema

> Status: **Phase 1 hardening — landed 2026-05-06** ·
> Companion to [`architecture.md`](../architecture.md), the
> [audit ledger ADR](../adr/0010-audit-ledger.md), and
> [`observability/slos.md`](slos.md) (when that document arrives).

Every span the platform emits opens off one of six canonical
`ActivitySource`s defined in
[`AiLibActivitySource`](../../src/AiLibrarian.Auditing/AiLibActivitySource.cs).
Sharing the registry from `AiLibrarian.Auditing` keeps audit rows and
trace spans addressable under the same correlation id without forcing
a new infrastructure project.

## Source registry

| Source name | Owner concern |
|---|---|
| `AiLibrarian.Search` | Hybrid retrieval (vector + full-text) |
| `AiLibrarian.Ingest` | Pipeline stages — blob open, skill canonicalize, persist, embed |
| `AiLibrarian.Llm` | LLM gateway calls (chat, embedding, rerank) |
| `AiLibrarian.Audit` | Audit-ledger writes |
| `AiLibrarian.Mcp` | MCP tool invocations |
| `AiLibrarian.Repository` | Source / department / chunk repository reads |

A subscriber registers all six with one call:

```csharp
services.AddOpenTelemetry().WithTracing(t =>
    t.AddSource(AiLibActivitySource.Names.All));
```

## Span name conventions

Names match the `event_type / event_subtype` shape used by
[`audit_events`](../adr/0010-audit-ledger.md). One activity per
discrete operation, no nested "step" spans inside a single SQL call.

| Pattern | Example | Notes |
|---|---|---|
| `ailib.search.<op>` | `ailib.search.hybrid` | One per query; tag with vector weight, hit count |
| `ailib.ingest.pipeline` | `ailib.ingest.pipeline` | Outer span for the whole worker pipeline |
| `ailib.ingest.skill.<name>` | `ailib.ingest.skill.markdown` | One per Skill invocation; child of `ingest.pipeline` |
| `ailib.llm.<provider>.<op>` | `ailib.llm.azure-openai.chat` | One per LLM call; tag with token counts |
| `ailib.audit.write` | `ailib.audit.write` | One per persisted event |
| `ailib.mcp.tool.<name>` | `ailib.mcp.tool.search` | One per MCP tool invocation |
| `ailib.repository.<kind>.<op>` | `ailib.repository.source.get` | One per repository read |

## Canonical attribute keys

Pinned in
[`AiLibActivitySource.Attributes`](../../src/AiLibrarian.Auditing/AiLibActivitySource.cs)
so dashboards stay parseable as the system grows. Use the constants —
hard-coded strings drift.

| Attribute | Type | Notes |
|---|---|---|
| `ailib.user.oid_hash` | string | SHA-256 of the Entra OID; never the raw OID. PII boundary. |
| `ailib.dept.id` | uuid string | Owning department of the operation. |
| `ailib.persona.id` | uuid string | Active persona (`app.persona_id` session var). Null for neutral-context calls. |
| `ailib.classification.max` | string | Highest classification touched: `Public` / `Internal` / `Confidential` / `Restricted`. |
| `ailib.correlation_id` | uuid string | Same value the audit row carries; threads spans across services. |
| `ailib.skill.name` | string | Skill plugin id (e.g. `markdown`, `docx`). |
| `ailib.source.id` | uuid string | Source row id when relevant. |
| `ailib.source.sha256` | string | Lower-hex SHA-256 of the canonicalized payload. |
| `ailib.chunk.count` | int | Number of chunks emitted by a skill. |
| `ailib.llm.provider` | string | Provider id (`azure-openai`, etc.). |
| `ailib.llm.model` | string | Deployment / model name. |
| `ailib.llm.tokens.in` | int | Prompt tokens. |
| `ailib.llm.tokens.out` | int | Completion tokens. |
| `ailib.llm.latency_ms` | int | Wall-clock latency of the LLM call. |
| `ailib.audit.event_type` | string | The audit-row `event_type`. |
| `ailib.audit.event_subtype` | string | The audit-row `event_subtype`. |
| `ailib.audit.criticality` | string | `Critical` or `BestEffort`. |
| `ailib.mcp.tool` | string | MCP tool name. |
| `ailib.search.hit_count` | int | Hybrid-search result count. |

## What never goes on a span

ADR 0010's content-capture policy applies to traces too:

- **No raw query text** — the user's prompt is metadata-protected.
- **No prompt or completion bodies** — LLM telemetry is metadata-only.
- **No raw OID** — hash before tagging.
- **No row contents** — chunk markdown, source titles, audit `details`
  payload all stay out of spans. The audit row is the durable record;
  spans are the operational signal.

## Subscriber configuration

The API and the IngestWorker both register the same source set. App
Insights subscribes via the existing
`Microsoft.ApplicationInsights.AspNetCore` package when
`ApplicationInsights:ConnectionString` is configured. OpenTelemetry's
ASP.NET Core and HttpClient instrumentations come along automatically
in the API.

The Portal currently only emits the auto-instrumentation spans
(`Microsoft.AspNetCore.*`, `System.Net.Http.*`); custom spans are
unnecessary because the Portal is a thin client over the API and
inherits the trace context via header propagation.

The MCP server runs as a stdio child process — span emission still
works, but no exporter is configured by default (Cursor / Claude
Desktop don't aggregate traces). Operators who want MCP-side tracing
add an OTLP exporter at the same registration site as the API.

## SLO / dashboard companions

The hardening plan calls for an `slos.md` and an Application Insights
workbook JSON in `deploy/observability/`. Both are follow-ups to this
schema doc; they reference these names + attributes directly so a
schema change here flows through dashboards without a separate
content review.
