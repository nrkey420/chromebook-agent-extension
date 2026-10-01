#!/usr/bin/env bash
# Runs the Google sync functions once, now, and reports what happened (docs/google-sync.md, step 4).
#
# Usage: infra/scripts/run-google-sync.sh <resource-group> <function-app-name> [function ...]
#   Functions default to: GoogleUserSync GoogleDeviceSync GoogleChromeAuditSync GoogleLoginAuditSync
# Optional, to wait for the runs and print dbo.SyncState (needs go-sqlcmd, `az login` as a SQL Entra user,
# and your IP allowed by the SQL firewall):
#   SQL_SERVER    deployment output sqlServerFqdn
#   SQL_DATABASE  deployment output sqlDatabaseName
#   WAIT_MINUTES  how long to wait for results (default 10)
set -euo pipefail
RG=${1:-}; APP=${2:-}
if [[ -z "$RG" || -z "$APP" ]]; then
  echo "usage: $0 <resource-group> <function-app-name> [function ...]" >&2
  exit 2
fi
shift 2
FUNCTIONS=("$@")
(( ${#FUNCTIONS[@]} )) || FUNCTIONS=(GoogleUserSync GoogleDeviceSync GoogleChromeAuditSync GoogleLoginAuditSync)

sync_name() {
  case "$1" in
    GoogleUserSync) echo GoogleUsers ;;
    GoogleDeviceSync) echo GoogleDevices ;;
    GoogleChromeAuditSync) echo 'GoogleAudit:chrome' ;;
    GoogleLoginAuditSync) echo 'GoogleAudit:login' ;;
    *) echo "$1" ;;
  esac
}

# 1. Find the app. The generic resource API works for every hosting plan, including Flex Consumption.
APP_ID=$(az resource show -g "$RG" -n "$APP" --resource-type Microsoft.Web/sites --query id -o tsv 2>/dev/null || true)
if [[ -z "$APP_ID" ]]; then
  echo "Function App '$APP' was not found in resource group '$RG' (or you are not signed in: run 'az login')." >&2
  echo "Function Apps in '$RG':" >&2
  az resource list -g "$RG" --resource-type Microsoft.Web/sites --query "[].name" -o tsv >&2 || true
  exit 1
fi

FUNC_HOST=$(az resource show --ids "$APP_ID" --query properties.defaultHostName -o tsv 2>/dev/null || true)
if [[ -z "$FUNC_HOST" ]]; then
  FUNC_HOST="$APP.azurewebsites.net"
  echo "Could not read the app's hostname from Azure; using $FUNC_HOST"
fi
echo "Function App: $APP ($FUNC_HOST)"

# 2. Check the Google settings before calling anything.
ADMIN_EMAIL=$(az functionapp config appsettings list -g "$RG" -n "$APP" --query "[?name=='GOOGLE_ADMIN_EMAIL'].value | [0]" -o tsv 2>/dev/null || true)
if [[ -z "$ADMIN_EMAIL" ]]; then
  echo "WARNING: GOOGLE_ADMIN_EMAIL is not set on the app, so every job will skip. Set the GOOGLE_ADMIN_EMAIL"
  echo "         repository variable and redeploy (docs/google-sync.md, step 3)."
else
  echo "GOOGLE_ADMIN_EMAIL: $ADMIN_EMAIL"
fi
KV_STATUS=$(az rest --method get --url "https://management.azure.com${APP_ID}/config/configreferences/appsettings/GOOGLE_SERVICE_ACCOUNT_JSON?api-version=2023-12-01" \
  --query "properties.status" -o tsv 2>/dev/null || true)
case "$KV_STATUS" in
  Resolved) echo "Service account key: resolved from Key Vault" ;;
  "") echo "Service account key: could not check the Key Vault reference status (continuing)" ;;
  *) echo "WARNING: the service account key reference is '$KV_STATUS', so every job will skip. Create the"
     echo "         GoogleServiceAccountKey secret and restart the app (docs/google-sync.md, step 3)." ;;
esac

# 3. Master key for the admin API.
KEY=$(az functionapp keys list -g "$RG" -n "$APP" --query masterKey -o tsv 2>/dev/null || true)
if [[ -z "$KEY" ]]; then
  KEY=$(az rest --method post --url "https://management.azure.com${APP_ID}/host/default/listkeys?api-version=2023-12-01" \
    --query masterKey -o tsv 2>/dev/null || true)
fi
if [[ -z "$KEY" ]]; then
  echo "Could not read the app's master key. You need Contributor (or Website Contributor) on the Function App." >&2
  exit 1
fi

# 4. Start each job. The admin API returns 202 straight away; the job runs in the background.
STARTED_UTC=$(date -u -d '-2 minutes' '+%Y-%m-%dT%H:%M:%S' 2>/dev/null || date -u -v-2M '+%Y-%m-%dT%H:%M:%S')
started=()
for f in "${FUNCTIONS[@]}"; do
  body=$(mktemp)
  code=$(curl -sS -o "$body" -w '%{http_code}' -X POST "https://$FUNC_HOST/admin/functions/$f" \
    -H "x-functions-key: $KEY" -H 'Content-Type: application/json' -d '{}' || echo 000)
  case "$code" in
    202) echo "$f: started"; started+=("$f") ;;
    404) echo "$f: NOT FOUND - this function is not deployed. The Google sync is in the collector code on the"
         echo "     branch with it; merge that to main (the deploy workflow deploys main) or deploy it, then retry." ;;
    401|403) echo "$f: HTTP $code - the master key was rejected." ;;
    000) echo "$f: could not reach https://$FUNC_HOST" ;;
    *) echo "$f: HTTP $code $(head -c 300 "$body")" ;;
  esac
  rm -f "$body"
done
(( ${#started[@]} )) || exit 1

# 5. Optionally wait for the results in dbo.SyncState.
if [[ -z "${SQL_SERVER:-}" || -z "${SQL_DATABASE:-}" ]] || ! command -v sqlcmd >/dev/null; then
  cat <<EOF

Started. Runs take seconds (pilot OU) to several minutes (whole domain). Then check, in SQL:
  SELECT SyncName, LastStatus, ItemsProcessed, LastRunUtc, LastMessage FROM dbo.SyncState ORDER BY SyncName;
No row for a job after 10 minutes means it skipped: check the warnings above, or the app's log stream.
(Set SQL_SERVER and SQL_DATABASE, with go-sqlcmd installed, to have this script wait and print the results.)
EOF
  exit 0
fi

names=$(for f in "${started[@]}"; do printf "'%s'," "$(sync_name "$f")"; done); names=${names%,}
query="SET NOCOUNT ON; SELECT SyncName, LastStatus, ItemsProcessed, CONVERT(varchar(19), LastRunUtc, 126), LEFT(COALESCE(LastMessage, ''), 300)
FROM dbo.SyncState WHERE SyncName IN ($names) AND LastRunUtc >= '$STARTED_UTC' ORDER BY SyncName;"
deadline=$(( $(date +%s) + ${WAIT_MINUTES:-10} * 60 ))
echo
echo "Waiting for results in dbo.SyncState (up to ${WAIT_MINUTES:-10} min)..."
while :; do
  if ! rows=$(sqlcmd -S "$SQL_SERVER" -d "$SQL_DATABASE" --authentication-method ActiveDirectoryDefault -h -1 -W -s ' | ' -b -Q "$query" 2>&1); then
    echo "Could not query SQL (firewall rule for your IP? signed in as a SQL Entra user?):" >&2
    echo "$rows" >&2
    exit 1
  fi
  done_count=$(grep -c '|' <<<"$rows" || true)
  if (( done_count >= ${#started[@]} )) || (( $(date +%s) >= deadline )); then break; fi
  sleep 20
done
echo "SyncName | Status | Items | RunUtc | Message"
echo "${rows:-(no results)}"
if (( done_count < ${#started[@]} )); then
  echo "Some jobs have no result yet: still running (large domain), or skipped because the Google settings are not resolved."
  exit 1
fi
grep -q '| FAILED |' <<<"$rows" && { echo "At least one job FAILED: see the message above and the Troubleshooting table in docs/google-sync.md."; exit 1; }
echo "All jobs succeeded."
