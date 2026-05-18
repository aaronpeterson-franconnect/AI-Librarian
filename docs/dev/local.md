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

## Phase B — full ingest pipeline (Azurite + SB emulator + IngestWorker) — v1 manually runnable

`docker-compose.ingest.yml` brings up the full upload→ingest round-trip
locally with Microsoft's official emulators (no Azure resources):

```bash
docker compose -f docker-compose.yml -f docker-compose.ingest.yml \
  up -d --build
bash scripts/live-smoke-ingest.sh
```

What this adds to the stack:

- **`azurite`** — Microsoft's official Azure Storage emulator (blob only). Port `10000` (host) / `10000` (container). Uses the well-known dev-mode account `devstoreaccount1` documented in the Azurite README.
- **`sb-emulator-db`** — SQL Server Edge (`mcr.microsoft.com/azure-sql-edge`) that the Service Bus emulator stores state in. No host port; internal-only.
- **`sb-emulator`** — Microsoft's official Service Bus emulator. Ports `5672` (AMQP) and `5300` (HTTP management). Pre-configured via `deploy/sb-emulator-config.json` with namespace `sbemulatorns` (the emulator's hard-coded value; non-modifiable) and a single queue `ingest-jobs`.
- **`ingest-worker`** — the `AiLibrarian.IngestWorker` .NET service, wired to all three.

The override also injects env vars into the `api` service so
`/api/portal/sources/upload` is wired to the local Azurite + SB.

### Phase B v2 follow-up (CI gate deferred)

The CI workflow for this round-trip was attempted but pulled out of
this slice. The smoke kept failing at the upload step with an Azure
Storage SDK error: `"No valid combination of account information
found."` — the SDK rejecting the Azurite connection string despite
the string being valid and well-known. The diagnostic chase consumed
several CI iterations without root-cause. Suspects:

- YAML / docker-compose env-var encoding mangling some part of the
  `AccountKey` (it contains `+`/`/`/`==` base64 padding) before it
  reaches the API process.
- The lazy-init order between `BlobServiceClient` connecting and
  `ServiceBusClient` connecting, where one's failure short-circuits
  the other.
- An `EndpointSuffix=` field the SDK requires when both
  `DefaultEndpointsProtocol` AND `BlobEndpoint` are set.

Each of these is faster to diagnose with Docker Desktop running
locally (sub-second iteration) than via CI cycles (~5 min per push).
The v2 slice should:

1. Bring the stack up locally
2. Reproduce the 502
3. Find the bad arg via `docker exec ailib-api env | grep BlobStorage`
4. Add the regression test
5. Re-add `.github/workflows/live-smoke-ingest.yml`

The infrastructure (compose, queue config, smoke script) all stay in
this v1 commit; only the auto-CI piece moves to v2.

### Layering with the LLM-mock profile

Combine both overrides if you want embeddings during ingest too:

```bash
docker compose \
  -f docker-compose.yml \
  -f docker-compose.ingest.yml \
  -f docker-compose.llm-mock.yml \
  up -d --build
```

Order matters: `llm-mock.yml` overrides `ingest.yml`'s env vars
where they conflict (the LLM-mock toggle wins on
`LlmGateway:Providers:azure-openai:Enabled`, etc.). For Phase B's
own CI gate the LLM stays off — the worker's
`IngestWorker__Processing__GenerateEmbeddings=false` env var skips
the embed step, so the smoke doesn't depend on the LLM provider.

### What this still doesn't cover

- **Wiki Maintainer cascade**. Phase 2 schema; not exercised by the
  ingest pipeline.
- **Skills.Pdf / Skills.Office**. The smoke uploads markdown; the
  PDF/Office skills are exercised by their own unit tests but not in
  the live ingest round-trip. Worth a follow-up that uploads a
  sample PDF and asserts chunks come through.

## Phase 2B — deterministic LLM mock (v1 shipped, CI gate deferred)

The Phase 2B mock landed as a *manually-runnable* dev tool.
`src/AiLibrarian.LlmMock/` ships a small ASP.NET Core service that
mimics Azure OpenAI's
`/openai/deployments/{deployment}/embeddings` endpoint with hash-seeded
1536-dim vectors. Same input → same vector across runs.

Bring it up locally as a compose override:

```bash
docker compose -f docker-compose.yml -f docker-compose.llm-mock.yml \
  up -d --build postgres migrations api llm-mock
```

`docker-compose.llm-mock.yml` injects six `LlmGateway:Providers:azure-openai:*`
env vars on the `api` service that flip the provider to Enabled +
point it at `http://llm-mock:8080`. The `LlmKernelFactory`
(`src/AiLibrarian.LlmGateway/Internal/LlmKernelFactory.cs`) detects the
`http://` scheme on the endpoint and routes through the **OpenAI**
client (not AzureOpenAI), which accepts a custom `HttpClient` with
`BaseAddress` — sidestepping the SDK's hard https-only check on the
AzureOpenAI client.

**`/api/search/hybrid` returns 200 with a `hits[]` envelope when the
mock is active**, instead of the 503 the no-mock workflow covers.

### What's NOT in v1 (Phase 2B-v2 follow-up)

A CI workflow that auto-runs the with-mock smoke on every PR was
removed before this slice merged because the SK OpenAI client's
base64-encoded-embedding response parser kept rejecting our mock's
output despite the bytes being valid base64. After 7 CI iterations
trying response-shape variations the issue was scoped down to
**something about how System.Text.Json serializes anonymous-typed
properties of `object` runtime type when the client uses
`JsonElement.GetBytesFromBase64()` on the receiving side**. The
fix likely involves either:

1. Returning a concrete typed record from the mock instead of an
   anonymous type with `object` properties; OR
2. Bypassing the SK OpenAI client entirely and writing a small
   `IEmbeddingProvider` implementation that talks to the mock with
   our own response shape — pulling the SK-OpenAI-SDK dance out of
   the loop.

Either is straightforward, but the slice closing the gate is its own
PR. Until then, the mock is exercised manually for development; the
no-mock Live Smoke + the live calibration workflow + the deployed
pilot's smoke continue to gate the rest.

### What the mock is NOT

- It doesn't preserve semantic similarity (random hashes don't), so
  retrieval ranking is meaningless against a real corpus. Good for
  "plumbing works"; not for "retrieval finds the right chunks".
- It only implements the embeddings endpoint. `/api/ask` (which
  needs chat completions) still 503s against the mock.

**For real retrieval quality testing**, point `HttpEvalBackend`
(`tests/AiLibrarian.Eval/Runner/HttpEvalBackend.cs`) at a deployed API
wired to real Azure OpenAI. The mock + the cloud-deployed API are
complementary: mock proves the gateway plumbing locally; real Azure
OpenAI proves the retrieval/synthesis quality on a schedule.

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
