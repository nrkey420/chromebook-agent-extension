#!/usr/bin/env bash
# Deploys infra/bicep/main.bicep.
# Required env vars:
#   SQL_ADMIN_PASSWORD  SQL admin password (Azure SQL complexity rules apply)
#   HMAC_KEY_B64        base64 HMAC secret for key id KEY1, e.g. `openssl rand -base64 32`
#                       (the same value goes in the extension's managed policy)
set -euo pipefail
RG=${1:-rg-chromebook-poc}
LOC=${2:-eastus}
: "${SQL_ADMIN_PASSWORD:?set SQL_ADMIN_PASSWORD}"
: "${HMAC_KEY_B64:?set HMAC_KEY_B64 (e.g. openssl rand -base64 32)}"
az group create -n "$RG" -l "$LOC" >/dev/null
az deployment group create -g "$RG" -f infra/bicep/main.bicep \
  -p @infra/bicep/main.parameters.json \
  -p location="$LOC" sqlAdminPassword="$SQL_ADMIN_PASSWORD" hmacKey="$HMAC_KEY_B64" \
  --query properties.outputs -o json
