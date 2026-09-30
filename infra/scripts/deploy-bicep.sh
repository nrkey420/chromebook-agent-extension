#!/usr/bin/env bash
# Deploys infra/bicep/main.bicep.
# Required env vars:
#   SQL_ADMIN_PASSWORD  SQL admin password (Azure SQL complexity rules apply)
#   HMAC_KEY_B64        base64 HMAC secret for key id KEY1, e.g. `openssl rand -base64 32`
#                       (the same value goes in the extension's managed policy)
# Optional:
#   KEYVAULT_ADMIN_OBJECT_ID  user/group object ID granted Key Vault Secrets Officer
#                             (defaults to the signed-in user; skipped for service principals)
# Secrets are stored in Key Vault; the Function App reads them via Key Vault references.
set -euo pipefail
RG=${1:-rg-chromebook-poc}
LOC=${2:-eastus}
: "${SQL_ADMIN_PASSWORD:?set SQL_ADMIN_PASSWORD}"
: "${HMAC_KEY_B64:?set HMAC_KEY_B64 (e.g. openssl rand -base64 32)}"
KV_ADMIN="${KEYVAULT_ADMIN_OBJECT_ID:-$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)}"
EXTRA_PARAMS=()
[[ -n "$KV_ADMIN" ]] && EXTRA_PARAMS=(keyVaultAdminObjectId="$KV_ADMIN")
az group create -n "$RG" -l "$LOC" >/dev/null
az deployment group create -g "$RG" -f infra/bicep/main.bicep \
  -p @infra/bicep/main.parameters.json \
  -p location="$LOC" sqlAdminPassword="$SQL_ADMIN_PASSWORD" hmacKey="$HMAC_KEY_B64" ${EXTRA_PARAMS[@]+"${EXTRA_PARAMS[@]}"} \
  --query properties.outputs -o json
