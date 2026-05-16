# Local dev — docker-compose stack

One command brings up Postgres + Liquibase migrations + API + Portal:

```bash
docker compose up --build
```

First boot takes 2–5 minutes (image builds for the api/portal services).
Subsequent boots take ~20 seconds. The stack starts in the order:

1. **`postgres`** — pgvector-enabled Postgres 16. Healthcheck via `pg_isready`.
2. **`migrations`** — Liquibase 4.29 runs `db/changelog/master.xml` against
   the database, then exits. The api + portal services wait for this to
   complete successfully (`depends_on: condition: service_completed_successfully`).
3. **`api`** — ASP.NET Core minimal API on `http://localhost:5071`.
   Postgres-backed endpoints work; LLM-backed endpoints return 503 until
   you configure an LLM provider (see below).
4. **`portal`** — Blazor Server on `http://localhost:5215`. Pages that
   browse departments and sources work. Upload returns 503 until Service
   Bus and Blob Storage are wired in (Phase B).

When all four containers are up, the Portal at `http://localhost:5215`
will render but show empty pages because the database has no seed data.

## Smoke-test recipe

Use the automated script — same one CI runs (`scripts/live-smoke.sh`):

```bash
docker compose up -d postgres migrations api
bash scripts/live-smoke.sh
```

If CI's Live Smoke job fails, you can reproduce locally with these two
commands without translating yaml to bash.

For manual probing, the steps the script automates are:

```bash
# 1. Verify the API is up. Should return JSON with auditStatus.CircuitState=Closed.
curl http://localhost:5071/health

# 2. Verify Postgres-backed endpoint. Returns [] until you insert a department.
curl http://localhost:5071/api/departments

# 3. Insert a test department via psql. The `app.is_authenticated`
#    + `app.is_employee` SET commands push an RLS session context so
#    INSERT passes the departments write predicate. `name` is the
#    citext machine identifier (lowercased); `display_name` is the
#    human-facing label.
docker exec -it ailib-postgres psql -U ailibrarian -d ailibrarian <<'SQL'
SET app.is_authenticated = 'true';
SET app.is_employee = 'true';
INSERT INTO departments (id, name, display_name)
VALUES ('11111111-1111-1111-1111-111111111111', 'engineering', 'Engineering')
ON CONFLICT (name) DO NOTHING;
SQL

# 4. Verify the department appears.
curl http://localhost:5071/api/departments
# → {"items":[{"id":"11111111-...","name":"engineering","displayName":"Engineering"}]}

# 5. Open Portal in a browser.
#    http://localhost:5215
#    The Sources page should now show the Engineering department.
```

## What's in this stack vs. what isn't

| Service / surface | In compose? | Notes |
|---|---|---|
| Postgres + pgvector | Yes | Same image the RLS battery uses |
| Liquibase migrations | Yes | Runs once on startup, then exits |
| API (Postgres endpoints) | Yes | `/api/departments`, `/api/sources`, `/api/audit`, `/health` |
| API (LLM endpoints) | Returns 503 | Set `LlmGateway__Providers__azure_openai__*` env vars to enable |
| Portal (browse pages) | Yes | Dev-mode auth (no Entra) |
| Portal upload pipeline | Returns 503 | Needs Service Bus + Blob Storage (Phase B) |
| IngestWorker | **Not in compose** | Phase B — no permissive Service Bus emulator available locally |
| MCP server | **Not in compose** | Runs over stdio; `dotnet run --project src/AiLibrarian.Mcp` against the compose API |
| WikiMaintainer | **Not in compose** | Hosted-service inside the API; cascade off by default |

## Enabling LLM endpoints

To exercise `/api/search/hybrid` and `/api/ask` you need Azure OpenAI keys.
Add an `.env` file at the repo root (gitignored), then reference it from
`docker-compose.yml` with `env_file:`. Example `.env`:

```
LlmGateway__Providers__azure-openai__Enabled=true
LlmGateway__Providers__azure-openai__Endpoint=https://YOUR-RESOURCE.openai.azure.com/
LlmGateway__Providers__azure-openai__ApiKey=YOUR_KEY
LlmGateway__Providers__azure-openai__ChatDeployment=gpt-4o-mini
LlmGateway__Providers__azure-openai__EmbeddingDeployment=text-embedding-3-large
Search__EmbeddingDeployment=text-embedding-3-large
```

The Azure OpenAI provider's `Enabled=true` flag is what flips the API
out of "no LLM provider configured → 503" mode.

## Resetting the database

```bash
docker compose down -v   # stops + removes the postgres-data volume
docker compose up        # next start re-runs every migration from scratch
```

Use this when you've edited a Liquibase changeset in place and need to
re-apply from a clean slate. Liquibase rejects edits-in-place against a
populated database (checksum mismatch); the test bootstrap doesn't care,
but compose does because it persists state.

## Inspecting the stack

```bash
docker compose ps              # status of each service
docker compose logs api        # API logs (look for ASP.NET startup + audit probe lines)
docker compose logs migrations # full Liquibase output, useful when migrations fail
docker compose logs postgres   # Postgres logs (pg_isready healthcheck failures show here)
docker exec -it ailib-postgres psql -U ailibrarian -d ailibrarian
```

## Phase 2B — deterministic LLM mock (now shipped)

The Phase 2B mock landed. `src/AiLibrarian.LlmMock/` ships a small
ASP.NET Core service that mimics Azure OpenAI's
`/openai/deployments/{deployment}/embeddings` endpoint with hash-seeded
1536-dim vectors. Same input → same vector across runs, which is the
property that lets the eval harness produce reproducible retrieval
rankings without paying for real LLM calls.

Bring it up as a compose override:

```bash
docker compose -f docker-compose.yml -f docker-compose.llm-mock.yml \
  up -d --build postgres migrations api llm-mock
```

Then `/api/search/hybrid` returns **200 with a `hits[]` envelope**
instead of the 503 the no-mock smoke covers. The
`scripts/live-smoke-llm.sh` script seeds a minimal department + user +
source + chunk and exercises the gate; same shape as
`scripts/live-smoke.sh` but for the LLM-on path.

**What the mock is NOT:**

- It doesn't preserve semantic similarity (random hashes don't),
  so retrieval ranking is meaningless against a real corpus. Good
  for "plumbing works"; not for "retrieval finds the right chunks".
- It only implements the embeddings endpoint. `/api/ask` (which
  needs chat completions) still 503s. Extending to chat is a
  follow-up.

**For real retrieval quality testing**, point `HttpEvalBackend`
(`tests/AiLibrarian.Eval/Runner/HttpEvalBackend.cs`) at a deployed API
wired to real Azure OpenAI. The mock + the cloud-deployed API are
complementary: mock proves the gateway plumbing on every PR; real
Azure OpenAI proves the retrieval/synthesis quality on a schedule.

## Known limitations (Phase A)

- **No Service Bus** — IngestWorker is omitted because Azure Service Bus
  has no free local emulator (Microsoft's emulator container requires a
  SQL Server sidecar + accept-EULA flag and isn't gratis-friendly enough
  for "one command brings the stack up"). The Phase B slice will either
  add the emulator behind a `--profile ingest` flag or add a polling
  fallback to the worker.
- **No Blob Storage** — Azurite is intentionally absent for the same
  reason. Phase B will add it because Portal upload is blocked on Blob.
- **No LLM provider** — keys are user-supplied via `.env` (above). Phase
  A is "the stack stands up"; Phase B layers ingest + LLM.
- **No reverse proxy / TLS** — direct port mappings only. Adding nginx
  is deferred until there's a reason (e.g. testing OIDC redirects).
