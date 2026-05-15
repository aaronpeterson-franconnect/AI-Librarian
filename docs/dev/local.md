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

## Phase 2B — deterministic LLM mock for full retrieval smoke

The current Live Smoke workflow exercises Postgres-only endpoints
(`/health`, `/api/departments`, `/api/audit/recent`) plus the
documented 503 contract for `/api/search/hybrid` when no LLM is
configured. It does NOT catch regressions inside the retrieval or
synthesis pipeline because those need an embedding/chat provider.

Two options to add real-retrieval coverage:

1. **GitHub secrets + Azure OpenAI** — schedule a nightly workflow
   that pulls keys from `${{ secrets.AZURE_OPENAI_* }}` and wires
   them through `env_file:` into the compose api service. Burns
   real tokens (~$0.50/run for the 5-case corpus) and only runs on
   trusted contexts (won't fire on forked-PR runs).
2. **Deterministic embedding mock** — a tiny service in compose
   that responds to `POST /openai/deployments/*/embeddings` with a
   hash-seeded float vector. The API can't tell it's not Azure
   OpenAI; pgvector cosine-similarity stays meaningful because the
   same input produces the same vector. Zero LLM cost, runs on
   every PR including forks.

Recommend (2) for the every-PR signal and (1) as a nightly tripwire
that confirms the real provider hasn't drifted. Tracked as Phase 2B
of the docker-compose dev-stack work.

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
