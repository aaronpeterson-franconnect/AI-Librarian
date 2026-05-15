#!/usr/bin/env bash
#
# Live API smoke against the docker-compose dev stack. Exercises the
# real wiring -- migrations on a fresh Postgres, audit-writer startup
# probe against the live audit_events table, a route round-tripping
# through RLS, the 503 contract for the LLM-unconfigured path.
#
# Run sequence:
#   1. /health green + audit-writer circuit Closed
#   2. Startup probe wrote system.audit.writer.ready
#   3. psql INSERT into departments (RLS session-var pushdown path)
#   4. /api/departments reflects the seeded row
#   5. /api/audit/recent shows the departments.list call was audited
#   6. /api/search/hybrid returns 503 with the documented detail
#      (gates against the endpoint silently degrading to an empty 200)
#
# Designed to run identically locally and on the CI runner. Locally:
#   docker compose up -d postgres migrations api
#   ./scripts/live-smoke.sh
#
# In CI, see .github/workflows/live-smoke.yml.

set -euo pipefail

API_BASE="${API_BASE:-http://localhost:5071}"
PG_CONTAINER="${PG_CONTAINER:-ailib-postgres}"
SMOKE_TIMEOUT="${SMOKE_TIMEOUT:-90}"

# pretty status output
fail() { echo "  FAIL: $*" >&2; exit 1; }
pass() { echo "  PASS: $*"; }

# Wait for /health to respond 200 within $SMOKE_TIMEOUT seconds.
# Surfaces as a "stack didn't come up" failure on CI rather than a
# cascade of cryptic curl errors.
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

echo "=== STEP 1: /health audit circuit is Closed ==="
H=$(curl -sf "${API_BASE}/health")
echo "  body: ${H}"
echo "${H}" | grep -q '"circuitState":"Closed"' \
	&& pass "audit-writer circuit closed (probe ran against real audit_events table)" \
	|| fail "audit circuit not closed -- the startup probe didn't pass against Postgres"

echo "=== STEP 2: system.audit.writer.ready event was emitted at startup ==="
A=$(curl -sf "${API_BASE}/api/audit/recent?limit=50")
echo "${A}" | grep -q '"eventSubtype":"system.audit.writer.ready"' \
	&& pass "startup probe wrote its readiness audit row" \
	|| fail "no system.audit.writer.ready in audit_events -- startup observability regressed"

echo "=== STEP 3: seed engineering department via psql ==="
# -i on docker exec keeps stdin open so the heredoc reaches psql.
# Without it psql gets empty stdin, runs no SQL, and exits 0 -- a
# silent no-op that's hard to spot because the next assertion is what
# breaks. -v ON_ERROR_STOP=1 fails fast on any SQL error.
docker exec -i "${PG_CONTAINER}" psql -U ailibrarian -d ailibrarian -q -v ON_ERROR_STOP=1 <<'SQL'
SET app.is_authenticated = 'true';
SET app.is_employee = 'true';
INSERT INTO departments (id, name, display_name)
VALUES ('11111111-1111-1111-1111-111111111111', 'engineering', 'Engineering')
ON CONFLICT (name) DO NOTHING;

-- Confirm the row landed BEFORE the script moves on -- otherwise an
-- inert seed (e.g. -i stripped, heredoc swallowed elsewhere) lets the
-- script reach STEP 4 and fail there with a confusing "RLS regressed"
-- message. We'd rather see "seed did nothing" attributed at the
-- correct step.
SELECT count(*) AS engineering_rows
  FROM departments
 WHERE name = 'engineering';
SQL
pass "INSERT into departments under RLS session-var pushdown succeeded"

echo "=== STEP 4: /api/departments reflects the seed ==="
D=$(curl -sf "${API_BASE}/api/departments")
echo "  body: ${D}"
echo "${D}" | grep -q '"name":"engineering"' \
	&& pass "department appears via API (Postgres read path intact)" \
	|| fail "department not visible via API -- RLS read predicate or routing regressed"

echo "=== STEP 5: the list call left an audit row ==="
A2=$(curl -sf "${API_BASE}/api/audit/recent?limit=5")
echo "${A2}" | grep -q '"eventSubtype":"departments.list"' \
	&& pass "departments.list audit event recorded" \
	|| fail "departments.list not in audit -- route-level audit middleware regressed"

echo "=== STEP 6: /api/search/hybrid 503 contract is stable ==="
S=$(curl -s -w "\n%{http_code}" -X POST "${API_BASE}/api/search/hybrid" \
	-H "Content-Type: application/json" -d '{"query":"smoke"}')
CODE=$(echo "${S}" | tail -n1)
BODY=$(echo "${S}" | head -n -1)
echo "  status: ${CODE}"
echo "  body:   ${BODY}"
# 503 with this exact detail is the contract; a silent-200 with empty
# results would mask "LlmGateway has no provider enabled" -- which is
# how /api/search/hybrid would degrade if someone "fixed" the embedding
# requirement without realising why it was there.
[ "${CODE}" = "503" ] \
	&& pass "503 returned when no LLM provider configured" \
	|| fail "expected 503 (LlmGateway not configured); got ${CODE} -- silent degradation regression"
echo "${BODY}" | grep -q 'LlmGateway embedding provider' \
	&& pass "503 detail still mentions LlmGateway embedding provider" \
	|| fail "503 detail text changed -- operators relying on this string for diagnosis will break"

echo
echo "=== ALL SMOKE ASSERTIONS PASSED ==="
