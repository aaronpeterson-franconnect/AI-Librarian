# ADR 0004 — All AI client access goes through one MCP server

> Status: **Accepted** · Date: 2026-04-29 · Deciders: Architect (initial proposal — to be ratified)

## Context

Many AI clients want to consume AI Librarian's knowledge: Cursor,
GitHub Copilot, Claude Desktop, ChatGPT Enterprise, Microsoft Teams,
internal Copilot Agents, the web portal itself, and whatever ships
next year. We need an interface that:

1. Speaks a standard protocol every modern AI client already supports
2. Exposes a stable, small set of tools
3. Enforces our identity and access model on every call
4. Audits every interaction
5. Allows us to evolve internally (data model, ranker, vector backend)
   without breaking clients

The Model Context Protocol (MCP), originated by Anthropic and now
broadly adopted, is the de facto standard. Cursor, Claude Desktop,
ChatGPT, and Microsoft's Copilot tooling all natively consume MCP
servers.

## Decision

We expose **exactly one MCP server**, implemented in .NET using the
official `ModelContextProtocol` C# SDK, as the single AI client
entry point. Tools:

| Tool | Purpose |
|---|---|
| `search` | Hybrid search (vector + full-text + wiki page) within authorized departments |
| `get_page` | Fetch a wiki page with claims, citations, metadata |
| `get_neighborhood` | Fetch a page plus its 1-hop link graph |
| `ask` | Free-form question; LLM-synthesized answer with citations and consulted-page summary |
| `cite` | Resolve a citation to its raw source span |
| `list_departments` | Departments visible to the caller |
| `list_recent_changes` | Ingest, edit, lint activity (caller-scoped) |
| `submit_source` (optional) | AI-client-initiated ingest, subject to standard approval rules |

Authentication: callers present an Entra-issued OAuth 2.1 / OIDC
bearer token. The MCP server validates it, resolves group membership
to authorized department IDs and per-role lists, and pushes those
into the Postgres session via
`SET LOCAL app.user_id = $1; SET LOCAL app.department_ids = $2; ...`
on every connection. RLS does the rest.

For workstation clients (Cursor, Claude Desktop, internal Copilot
agents) we ship a small cross-platform .NET binary,
**`AiLibrarian.Cli`**:

- Handles Entra device-code OAuth on first run
- Caches refresh tokens in OS-native secure storage (DPAPI on
  Windows, Keychain on macOS, libsecret on Linux)
- Exposes the MCP server as a localhost stdio bridge that the AI
  client points at via standard MCP server configuration
- Auto-refreshes tokens; prompts the user to re-authenticate when
  the refresh window expires

This is cleaner UX than asking each client to do device-code itself,
and it gives us one place to manage workstation-side concerns (token
storage, log forwarding, version checks).

The web portal and Teams bot use standard server-side OAuth flows
and do not need the CLI.

### `ask` audit policy — metadata only

Every `ask` call writes an audit event capturing:

- Caller user, role, department(s) in scope
- Model and provider
- Prompt and completion token counts
- Cost estimate
- Latency
- Retrieved page / chunk IDs (for "which sources informed this
  answer")
- Outcome (success / failure / partial)

We **do not** capture the prompt text or the completion text in
the audit ledger. Trade-off: we cannot reproduce a query without
the user's help; we sacrifice some "the LLM hallucinated X"
forensic capability. We gain a cleaner privacy posture and reduce
the surface area for accidental sensitive-data retention.

If diagnostic content capture is needed (e.g., to debug a quality
regression), it is an opt-in mode at the department or user level,
visible to all users in that scope, and time-bounded.

### `submit_source` policy — strict

When `submit_source` is included as an MCP tool:

- Every AI-submitted source enters the librarian approval queue
  regardless of the department's auto-approve policy
- Per-caller rate limit (default 10 submissions per hour per user;
  configurable system-wide and per-department)
- The submission is tagged in the audit ledger with the AI client
  identity (Cursor, Claude Desktop, etc.) for separate analytics

### Client-tier expectations (ADR 0012)

Every AI client connecting through MCP is expected to be on an
enterprise-tier agreement with no-training and bounded-retention
data-handling guarantees. AI Librarian cannot enforce the client's
licensing arrangement at the protocol level, but:

- The workstation `AiLibrarian.Cli` records the connecting client's
  identity (user-agent, process name) on every session and emits an
  `mcp.session.client_identified` audit event.
- The CLI emits a non-blocking warning to the user if it detects a
  client outside the approved-clients list (e.g., consumer ChatGPT
  Desktop, free-tier Cursor). The connection proceeds.
- The approved-clients list lives in `docs/llm-providers.md` along
  with the approved-providers list, and is operator-maintained.
- Security periodically reviews the audit ledger for client-identity
  anomalies.

See ADR 0012 for the full requirement and rationale.

### Cross-department link rendering (Q14 closed; updated for ADR 0011 amendment)

After the ADR 0011 amendment, classification is the default access
boundary. Most cross-department reads work without restriction
because most content is `Internal`. The render-time gating below
only fires for `Confidential` and `Restricted` targets the caller
cannot reach:

- **Internal link target** (any caller authenticated): rendered
  normally. Caller clicks through and reads the page.
- **Confidential or Restricted link target the caller can access**
  (member of owning department, or shared via `source_shares`):
  rendered normally.
- **Confidential or Restricted link target the caller cannot
  access**: rendered as `[restricted: {DepartmentName}]`. Caller
  knows something exists but cannot see its content. Same for
  citations: `[citation: restricted]` when the cited source is
  inaccessible.
- **`get_neighborhood`**: linked pages the caller cannot access
  appear as `{ id, department_name, status: "restricted" }` —
  never title, never content. Internal links return full metadata.
- **Page facets** (ADR 0006): when a page has multiple facets,
  `read_page` returns the highest-classification facet the caller
  can access. The Internal facet of a page is readable
  cross-department; the Confidential facet is gated to the owning
  department and any shared departments. The caller does not see
  what facets exist beyond their level.

This is a structural property of the rendering pipeline, not a
client-side filter. The same RLS predicates that gate the database
also drive the rendering decisions — there is no separate
client-side allow-list to bypass.

The MCP server **does not embed** business logic. It is a thin
adapter over the same domain services the web portal calls (search
service, wiki service, cite service, ask service). The web portal
and the MCP server are interchangeable consumers of the same
underlying API.

`ask` is the only tool that synthesizes through the LLM; the rest
return structured data the calling AI can reason over. Every `ask`
call is fully audited (tokens, model, retrieved-page IDs, prompt and
completion content under retention policy).

## Consequences

### Easier

- Adding a new AI client = registering an MCP server endpoint with it.
  No new API surface to design, no new auth flow.
- Cursor, Copilot, Claude Desktop, and the web portal share one
  permission model and one audit trail.
- Internal evolution (e.g., swapping vector backends) stays invisible
  to clients.
- Abuse / cost / rate-limit controls live in one place.

### Harder

- MCP is a young protocol. Some client implementations are still
  immature, especially around streaming and large-payload handling.
- The C# SDK is newer than the TypeScript / Python SDKs. Mitigation:
  pin versions, write integration tests against Cursor and Claude
  Desktop, contribute back fixes if needed.
- We must keep the tool surface small and stable. New tools require a
  versioning convention and deprecation policy.

### Risks

- MCP standard evolves and breaks compatibility. Mitigation: track
  the spec, version our server, support N and N-1 simultaneously
  during transitions.
- A poorly-written MCP client could spam `ask` and drive up cost.
  Mitigation: per-caller rate limits and per-department budget caps
  (Phase 4).

## Alternatives considered

### Custom REST or GraphQL API

Workable but every AI client would need a custom integration. We'd
build that integration N times for N clients. MCP gives us one
integration N times.

### Multiple MCP servers (one per department)

Rejected. Identity/audit/policy enforcement would be duplicated and
inconsistent. One server with strong RLS does this better.

### Embed AI Librarian inside Microsoft Copilot Studio / Power Platform

Overcommits to one client and forecloses on others. The MCP layer
keeps us neutral.

## References

- [Model Context Protocol specification](https://modelcontextprotocol.io)
- [Microsoft MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- ADR 0005 — Role-based RLS with Entra
- ADR 0010 — Audit ledger
