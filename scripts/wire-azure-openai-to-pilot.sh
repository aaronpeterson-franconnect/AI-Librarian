#!/usr/bin/env bash
#
# One-shot script that takes an Azure OpenAI endpoint + api key +
# deployment names and pushes them into the deployed pilot's API
# and IngestWorker Container Apps as env vars. After this runs:
#   /api/search/hybrid -> returns 200 with real embeddings (not 503)
#   /api/ask           -> returns synthesized answer (not 503)
#
# Run after:
#   1. Azure OpenAI resource exists (quota approved + provisioned)
#   2. Two deployments exist: gpt-4o-mini (chat) and
#      text-embedding-3-large (embeddings)
#   3. You have the resource endpoint + an api key
#
# Usage:
#   bash scripts/wire-azure-openai-to-pilot.sh \
#     --endpoint 'https://my-aoai.openai.azure.com/' \
#     --api-key 'YOUR_KEY' \
#     --chat-deployment gpt-4o-mini \
#     --embedding-deployment text-embedding-3-large
#
# The key is passed as a CLI argument rather than read from stdin or
# an env-var prompt so the operator's command history has a clear
# audit trail of the operation. Rotate the key in Azure portal after
# verifying the deploy works, to invalidate the value that briefly
# lived in your shell history.

set -euo pipefail

RG="${RG:-rg-ailib-pilot}"

# Argument parser (no external deps).
ENDPOINT=""
API_KEY=""
CHAT_DEPLOYMENT=""
EMBEDDING_DEPLOYMENT=""

while [ $# -gt 0 ]; do
	case "$1" in
		--endpoint) ENDPOINT="$2"; shift 2 ;;
		--api-key) API_KEY="$2"; shift 2 ;;
		--chat-deployment) CHAT_DEPLOYMENT="$2"; shift 2 ;;
		--embedding-deployment) EMBEDDING_DEPLOYMENT="$2"; shift 2 ;;
		*) echo "Unknown arg: $1"; exit 1 ;;
	esac
done

# Required-arg checks. Empty endpoint or key is the typical "operator
# forgot a flag" failure mode; surface the missing one explicitly.
for var in ENDPOINT API_KEY CHAT_DEPLOYMENT EMBEDDING_DEPLOYMENT; do
	if [ -z "${!var}" ]; then
		echo "Missing required argument: --${var,,}"
		echo "Run with --help for usage."
		exit 1
	fi
done

# Trim trailing slash from endpoint -- the Bicep template normalizes
# the same way (NormalizeEndpoint in LlmKernelFactory), but if the
# operator pastes `https://x.openai.azure.com/` here AND the URL
# concatenation in the SDK isn't careful, the resulting URL ends up
# `https://x.openai.azure.com//openai/...` which fails.
ENDPOINT="${ENDPOINT%/}"

API_APP=$(az deployment group show -g "$RG" -n main \
	--query 'properties.outputs.apiContainerAppName.value' -o tsv)
WORKER_APP=$(az deployment group show -g "$RG" -n main \
	--query 'properties.outputs.ingestWorkerContainerAppName.value' -o tsv)

echo "API app:    $API_APP"
echo "Worker app: $WORKER_APP"
echo "Endpoint:   $ENDPOINT (trimmed trailing slash)"
echo "Chat:       $CHAT_DEPLOYMENT"
echo "Embedding:  $EMBEDDING_DEPLOYMENT"
echo ""

echo "=== Updating API container app ==="
az containerapp update --name "$API_APP" --resource-group "$RG" \
	--set-env-vars \
		"LlmGateway__Providers__azure-openai__Enabled=true" \
		"LlmGateway__Providers__azure-openai__Endpoint=$ENDPOINT" \
		"LlmGateway__Providers__azure-openai__ApiKey=$API_KEY" \
		"LlmGateway__Providers__azure-openai__ChatDeployment=$CHAT_DEPLOYMENT" \
		"LlmGateway__Providers__azure-openai__EmbeddingDeployment=$EMBEDDING_DEPLOYMENT" \
		"Search__EmbeddingDeployment=$EMBEDDING_DEPLOYMENT" \
	--output none
echo "API updated."

echo "=== Updating Worker container app ==="
az containerapp update --name "$WORKER_APP" --resource-group "$RG" \
	--set-env-vars \
		"LlmGateway__Providers__azure-openai__Enabled=true" \
		"LlmGateway__Providers__azure-openai__Endpoint=$ENDPOINT" \
		"LlmGateway__Providers__azure-openai__ApiKey=$API_KEY" \
		"LlmGateway__Providers__azure-openai__ChatDeployment=$CHAT_DEPLOYMENT" \
		"LlmGateway__Providers__azure-openai__EmbeddingDeployment=$EMBEDDING_DEPLOYMENT" \
		"IngestWorker__Embeddings__ModelDeploymentName=$EMBEDDING_DEPLOYMENT" \
	--output none
echo "Worker updated."

echo ""
echo "=== Container Apps will roll forward to a new revision (~30-60s) ==="
echo "Wait briefly, then smoke:"
echo ""
API_FQDN=$(az deployment group show -g "$RG" -n main \
	--query 'properties.outputs.apiContainerAppFqdn.value' -o tsv)
echo "  curl https://$API_FQDN/health"
echo "  curl -X POST https://$API_FQDN/api/search/hybrid \\"
echo "       -H 'Content-Type: application/json' \\"
echo "       -d '{\"query\":\"first real embedding\"}'"
echo ""
echo "If /api/search/hybrid returns 200 with hits + a real"
echo "correlationId, the pilot is now LLM-enabled."
echo ""
echo "REMINDER: rotate the Azure OpenAI api key in the portal"
echo "(Resource -> Keys and Endpoint -> Regenerate Key1) within a"
echo "day or two -- the value briefly lived in your shell history."
