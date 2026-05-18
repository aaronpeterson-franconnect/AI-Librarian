#!/usr/bin/env bash
#
# Phase B live smoke: round-trip an upload through the full ingest
# pipeline against the compose stack with Azurite + Service Bus
# emulator + IngestWorker.
#
# What this gates that the other smokes can't:
#   - api -> Azurite blob write (BlobUploadService)
#   - api -> SB emulator enqueue (ServiceBusIngestJobPublisher)
#   - sb emulator -> worker dequeue (IngestServiceBusHostedService)
#   - worker -> Azurite blob read (AzureBlobContentOpener)
#   - worker -> skill plugin (the Markdown skill)
#   - worker -> Postgres write (source_chunks table)
#
# Plus the audit-row that proves every step left a trail.
#
# Designed to run locally:
#   docker compose -f docker-compose.yml -f docker-compose.ingest.yml \
#     up -d --build
#   ./scripts/live-smoke-ingest.sh
#
# In CI, see .github/workflows/live-smoke-ingest.yml.

set -euo pipefail

API_BASE="${API_BASE:-http://localhost:5071}"
PG_CONTAINER="${PG_CONTAINER:-ailib-postgres}"
SMOKE_TIMEOUT="${SMOKE_TIMEOUT:-180}"
INGEST_WAIT="${INGEST_WAIT:-90}"

DEPT_ID='11111111-1111-1111-1111-111111111111'
CONTRIBUTOR_ID='22222222-2222-2222-2222-222222222222'

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

echo "=== STEP 1: seed department + contributor user ==="
# The upload's dev-mode RLS-override path requires a real users row
# matching contributorId; the FK on sources.contributed_by enforces it.
docker exec -i "${PG_CONTAINER}" psql -U ailibrarian -d ailibrarian -q -v ON_ERROR_STOP=1 <<SQL
SET app.is_authenticated = 'true';
SET app.is_employee = 'true';

INSERT INTO departments (id, name, display_name)
VALUES ('${DEPT_ID}', 'engineering', 'Engineering')
ON CONFLICT (name) DO NOTHING;

INSERT INTO users (id, email, display_name, is_employee)
VALUES ('${CONTRIBUTOR_ID}', 'ingest-smoke@example.com', 'Ingest Smoke', true)
ON CONFLICT (id) DO NOTHING;

SELECT count(*) AS dept_rows  FROM departments WHERE id = '${DEPT_ID}';
SELECT count(*) AS user_rows  FROM users       WHERE id = '${CONTRIBUTOR_ID}';
SQL
pass "department + contributor seeded"

# Brief settle so the api's lazy BlobServiceClient + ServiceBusClient
# have a fresh emulator to talk to. Both connect on first call; both
# emulators take a few seconds after /health is reachable. 15s is
# more generous than necessary in steady-state but catches the cold
# CI startup race.
sleep 15

echo "=== STEP 2: upload a sample markdown file via /api/portal/sources/upload ==="
# Sample content the Markdown skill will chunk. ~200 chars so we get
# a single chunk back (worker chunker defaults to ~1500 chars/chunk).
SAMPLE_FILE="$(mktemp --suffix=.md)"
cat >"${SAMPLE_FILE}" <<'MARKDOWN'
# Phase B Smoke Sample

This is the test document the ingest pipeline picks up. The Markdown
skill parses it, splits it into chunks, and writes the chunks to the
source_chunks table.

If this file's content shows up in source_chunks.content_markdown,
the full upload-to-DB round trip works end-to-end.
MARKDOWN

# Verbose curl + max-time so a hang doesn't masquerade as a 502.
# stderr captured separately so the curl-side error is visible if
# the HTTP layer never returns a status code.
UPLOAD_OUT=$(mktemp)
UPLOAD_ERR=$(mktemp)
CODE=$(curl --max-time 60 -sS -o "${UPLOAD_OUT}" -w "%{http_code}" \
	-X POST "${API_BASE}/api/portal/sources/upload" \
	-F "file=@${SAMPLE_FILE};type=text/markdown" \
	-F "departmentId=${DEPT_ID}" \
	-F "classification=Internal" \
	-F "title=Phase B Smoke Sample" \
	-F "contributorId=${CONTRIBUTOR_ID}" 2>"${UPLOAD_ERR}" || true)
BODY=$(head -c 800 "${UPLOAD_OUT}")
ERR=$(head -c 800 "${UPLOAD_ERR}")
echo "  status: ${CODE}"
echo "  body:   ${BODY}"
echo "  stderr: ${ERR}"
rm -f "${UPLOAD_OUT}" "${UPLOAD_ERR}"
[ "${CODE}" = "200" ] \
	&& pass "/api/portal/sources/upload returned 200" \
	|| fail "upload returned ${CODE} -- check api logs (blob/sb config?)"

# Extract the source id from the upload response. Field is
# `sourceId` (camelCase from STJ Web defaults).
SOURCE_ID=$(echo "${BODY}" | grep -oE '"sourceId":"[^"]+"' | head -1 | sed 's/.*"sourceId":"\([^"]*\)".*/\1/')
if [ -z "${SOURCE_ID}" ]; then
	fail "couldn't parse sourceId from upload response"
fi
echo "  source id: ${SOURCE_ID}"

rm -f "${SAMPLE_FILE}"

echo "=== STEP 3: wait for the ingest worker to write source_chunks (max ${INGEST_WAIT}s) ==="
START=$(date +%s)
CHUNK_COUNT=0
while true; do
	CHUNK_COUNT=$(docker exec -i "${PG_CONTAINER}" psql -U ailibrarian -d ailibrarian -tA \
		-c "SELECT count(*) FROM source_chunks WHERE source_id = '${SOURCE_ID}'" 2>/dev/null || echo 0)
	if [ "${CHUNK_COUNT:-0}" -ge 1 ] 2>/dev/null; then
		break
	fi
	NOW=$(date +%s)
	if [ $((NOW - START)) -gt "${INGEST_WAIT}" ]; then
		fail "no source_chunks for source ${SOURCE_ID} after ${INGEST_WAIT}s -- worker stuck?"
	fi
	sleep 2
done
echo "  worker wrote ${CHUNK_COUNT} chunk(s) after $(( $(date +%s) - START ))s"
pass "ingest worker processed the upload"

echo "=== STEP 4: verify chunk content includes our sample text ==="
SAMPLE_HIT=$(docker exec -i "${PG_CONTAINER}" psql -U ailibrarian -d ailibrarian -tA \
	-c "SELECT count(*) FROM source_chunks WHERE source_id = '${SOURCE_ID}' AND content_markdown LIKE '%full upload-to-DB round trip%'")
[ "${SAMPLE_HIT:-0}" -ge 1 ] 2>/dev/null \
	&& pass "chunk content matches the uploaded sample" \
	|| fail "no chunk contained the sample's marker phrase -- skill plugin or storage issue"

echo "=== STEP 5: verify audit-events row for the upload ==="
# Route-level audit on upload would emit an event with eventType=source
# and a targetId equal to the source id. The audit-writer fires
# best-effort but should have recorded by the time we get here.
AUDIT_HIT=$(curl -sf "${API_BASE}/api/audit/recent?limit=50" | grep -oE '"eventType":"source"' | wc -l)
[ "${AUDIT_HIT:-0}" -ge 1 ] 2>/dev/null \
	&& pass "audit-events contains a source-related row" \
	|| fail "no source-related audit row -- audit middleware regressed"

echo
echo "=== ALL PHASE B INGEST SMOKE ASSERTIONS PASSED ==="
