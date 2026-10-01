#!/usr/bin/env bash
# Deploys infra/bicep/main.bicep.
# Required env vars:
#   HMAC_KEY_B64        base64 HMAC secret for key id KEY1, e.g. `openssl rand -base64 32`
#                       (the same value goes in the extension's managed policy; stored in Key Vault)
# Optional env vars (default to the signed-in `az` user; required when running as a service principal):
#   SQL_ENTRA_ADMIN_OBJECT_ID   object ID of the Azure SQL Entra admin (a group is recommended)
#   SQL_ENTRA_ADMIN_NAME        its display name / UPN
#   SQL_ENTRA_ADMIN_TYPE        User | Group | Application (default User)
#   KEYVAULT_ADMIN_OBJECT_ID    user/group granted Key Vault Secrets Officer
# Optional Google sync settings (docs/google-sync.md); the sync stays off without GOOGLE_ADMIN_EMAIL:
#   GOOGLE_ADMIN_EMAIL          Workspace admin the sync service account impersonates
#   GOOGLE_DEVICE_ORG_UNIT      limit the device sync to one OU (e.g. the pilot OU)
#   GOOGLE_STUDENT_ID_SOURCE    none | emailLocalPart | externalId[:type] | customSchema:Schema.Field
set -euo pipefail
RG=${1:-rg-chromebook-poc}
LOC=${2:-eastus}
: "${HMAC_KEY_B64:?set HMAC_KEY_B64 (e.g. openssl rand -base64 32)}"

ME_ID="" ME_UPN=""
if [[ -z "${SQL_ENTRA_ADMIN_OBJECT_ID:-}" || -z "${KEYVAULT_ADMIN_OBJECT_ID:-}" ]]; then
  read -r ME_ID ME_UPN < <(az ad signed-in-user show --query "[id, userPrincipalName]" -o tsv 2>/dev/null | tr '\n' ' ') || true
fi
SQL_ADMIN_ID=${SQL_ENTRA_ADMIN_OBJECT_ID:-$ME_ID}
SQL_ADMIN_NAME=${SQL_ENTRA_ADMIN_NAME:-$ME_UPN}
SQL_ADMIN_TYPE=${SQL_ENTRA_ADMIN_TYPE:-User}
KV_ADMIN=${KEYVAULT_ADMIN_OBJECT_ID:-$ME_ID}
if [[ -z "$SQL_ADMIN_ID" || -z "$SQL_ADMIN_NAME" ]]; then
  echo "Set SQL_ENTRA_ADMIN_OBJECT_ID and SQL_ENTRA_ADMIN_NAME (could not read the signed-in user)." >&2
  exit 1
fi

EXTRA_PARAMS=()
[[ -n "$KV_ADMIN" ]] && EXTRA_PARAMS+=(keyVaultAdminObjectId="$KV_ADMIN")
# Always passed, so a redeploy keeps the Google settings in step with what the caller provides.
EXTRA_PARAMS+=(googleAdminEmail="${GOOGLE_ADMIN_EMAIL:-}" googleDeviceOrgUnit="${GOOGLE_DEVICE_ORG_UNIT:-}")
[[ -n "${GOOGLE_STUDENT_ID_SOURCE:-}" ]] && EXTRA_PARAMS+=(googleStudentIdSource="$GOOGLE_STUDENT_ID_SOURCE")
az group create -n "$RG" -l "$LOC" >/dev/null
az deployment group create -g "$RG" -f infra/bicep/main.bicep \
  -p @infra/bicep/main.parameters.json \
  -p location="$LOC" hmacKey="$HMAC_KEY_B64" \
     sqlEntraAdminObjectId="$SQL_ADMIN_ID" sqlEntraAdminName="$SQL_ADMIN_NAME" sqlEntraAdminPrincipalType="$SQL_ADMIN_TYPE" \
     ${EXTRA_PARAMS[@]+"${EXTRA_PARAMS[@]}"} \
  --query properties.outputs -o json
