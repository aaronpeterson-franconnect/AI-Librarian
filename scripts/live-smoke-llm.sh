#!/usr/bin/env bash
#
# Live API smoke against the docker-compose stack with the LlmMock
# embedding service active. Asserts the same Postgres-only contracts
# scripts/live-smoke.sh covers, PLUS one new gate: /api/search/hybrid
# returns 200 with a hits[] array (instead of the 503 the no-mock
# smoke asserts).
#
# Why this is a separate script from live-smoke.sh: that script gates
# the "503 when LLM unconfigured" contract -- intentional, prevents
# silent degradation if someone removes the embedding-required guard.
# This script gates the "200 when LLM provider IS configured" contract
# -- prevents regression when someone changes the search path to fail
# under a working provider. Both gates protect orthogonal behaviors.
#
# Designed to run locally:
#   docker compose -f docker-compose.yml -f docker-compose.llm-mock.yml \
#     up -d --build postgres migrations api llm-mock
#   ./scripts/live-smoke-llm.sh
#
# In CI, see .github/workflows/live-smoke-llm.yml.

set -euo pipefail

API_BASE="${API_BASE:-http://localhost:5071}"
MOCK_BASE="${MOCK_BASE:-http://localhost:9080}"
PG_CONTAINER="${PG_CONTAINER:-ailib-postgres}"
SMOKE_TIMEOUT="${SMOKE_TIMEOUT:-120}"

fail() { echo "  FAIL: $*" >&2; exit 1; }
pass() { echo "  PASS: $*"; }

echo "=== Waiting for ${API_BASE}/health (max ${SMOKE_TIMEOUT}s) ==="
START=$(date +%s)
until curl -sf "${API_BASE}/health" -o /dev/null; do
	NOW=$(date +%s)
	if [ $((NOW - START)) -gt "${SMOKE_TIMEOUT}" ]; then
		fail "API did not become ready within ${SMOKE_TIMEOUT}s"
	fi
	sleep 2
done
echo "  api ready after $(( $(date +%s) - START ))s"

echo "=== STEP 1: llm-mock health probe ==="
M=$(curl -sf "${MOCK_BASE}/health" || true)
echo "  body: ${M}"
echo "${M}" | grep -q '"mock":"azure-openai-embeddings"' \
	&& pass "llm-mock health endpoint responds" \
	|| fail "llm-mock not reachable at ${MOCK_BASE} -- did the compose override get applied?"

echo "=== STEP 2: /health audit circuit is Closed ==="
H=$(curl -sf "${API_BASE}/health")
echo "  body: ${H}"
echo "${H}" | grep -q '"circuitState":"Closed"' \
	&& pass "audit-writer circuit closed against Postgres" \
	|| fail "audit circuit not closed -- the startup probe didn't pass"

echo "=== STEP 3: seed engineering department ==="
# Same seed shape as scripts/live-smoke.sh. The corpus stays empty;
# this gate only asserts /api/search/hybrid returns 200 with an
# envelope, not that retrieval finds specific chunks. An empty hits[]
# is the expected envelope under an empty source_chunks table.
docker exec -i "${PG_CONTAINER}" psql -U ailibrarian -d ailibrarian -q -v ON_ERROR_STOP=1 <<'SQL'
SET app.is_authenticated = 'true';
SET app.is_employee = 'true';
INSERT INTO departments (id, name, display_name)
VALUES ('11111111-1111-1111-1111-111111111111', 'engineering', 'Engineering')
ON CONFLICT (name) DO NOTHING;
SELECT count(*) AS engineering_rows FROM departments WHERE name = 'engineering';
SQL
pass "engineering department seed inserted"

echo "=== STEP 4: /api/search/hybrid returns 200 with mock embeddings ==="
# With the mock active, the API:
#   1. Receives POST /api/search/hybrid {"query":"..."}
#   2. Calls the embedding provider (mock) -> gets a 1536-dim vector
#   3. Issues a hybrid query against Postgres + pgvector
#   4. Returns a JSON document with `correlationId`, `embeddingDeployment`,
#      and `hits` array (possibly empty).
S=$(curl -s -w "\n%{http_code}" -X POST "${API_BASE}/api/search/hybrid" \
	-H "Content-Type: application/json" \
	-d '{"query":"live smoke seed"}')
CODE=$(echo "${S}" | tail -n1)
BODY=$(echo "${S}" | head -n -1)
echo "  status: ${CODE}"
echo "  body (first 300 chars): ${BODY:0:300}"
[ "${CODE}" = "200" ] \
	&& pass "search returned 200 -- LLM gateway pipeline reached embeddings endpoint" \
	|| fail "expected 200 from /api/search/hybrid with mock active, got ${CODE} (body: ${BODY:0:200})"
echo "${BODY}" | grep -q '"embeddingDeployment":"text-embedding-3-large"' \
	&& pass "response carries the configured embedding deployment name" \
	|| fail "response missing embeddingDeployment -- the search route may not be using LlmGateway:Providers:azure-openai"
echo "${BODY}" | grep -q '"hits"' \
	&& pass "response envelope includes hits[] array" \
	|| fail "response missing hits[] -- contract drift in HybridSearchResponse shape"

echo
echo "=== ALL LLM-MOCK SMOKE ASSERTIONS PASSED ==="
