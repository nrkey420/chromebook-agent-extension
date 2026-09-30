#!/usr/bin/env bash
# Creates (or updates) the Sentinel pipeline for collector events and wires it to the Function App:
#   1. custom table ChromebookActivity_CL in the Log Analytics / Sentinel workspace
#   2. Data Collection Endpoint (DCE)
#   3. Data Collection Rule (DCR) whose stream matches the collector payload
#   4. (with FUNCTION_APP_NAME) grants the app's managed identity "Monitoring Metrics Publisher" on the DCR
#      and sets DCE_ENDPOINT / DCR_IMMUTABLE_ID / DCR_STREAM_NAME on the app
# Columns come from infra/sentinel/chromebook-activity-schema.json (kept in sync with PayloadNormalizer by tests).
# All calls are PUTs, so re-running is safe. Requires az (logged in) and python3.
#
# Usage: create-dce-dcr.sh <resource-group> <location> <workspace-resource-id> [function-app-name] [dce-name] [dcr-name]
#   location must be the workspace's region (a DCR must be in the same region as its destination workspace).
set -euo pipefail

RG=${1:?resource group}
LOCATION=${2:?location (the workspace region, e.g. eastus)}
WORKSPACE_ID=${3:?log analytics workspace resource id}
FUNCTION_APP_NAME=${4:-}
DCE_NAME=${5:-chromebook-dce}
DCR_NAME=${6:-chromebook-dcr}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCHEMA="$SCRIPT_DIR/../sentinel/chromebook-activity-schema.json"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

SUB=$(az account show --query id -o tsv)
TABLE=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['tableName'])" "$SCHEMA")
STREAM=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['streamName'])" "$SCHEMA")
DCE_ID="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Insights/dataCollectionEndpoints/$DCE_NAME"
DCR_ID="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Insights/dataCollectionRules/$DCR_NAME"

python3 - "$SCHEMA" "$TMP" "$LOCATION" "$WORKSPACE_ID" "$DCE_ID" <<'PY'
import json, sys, os
schema_path, out, location, workspace_id, dce_id = sys.argv[1:6]
schema = json.load(open(schema_path))
cols = schema["columns"]
table = {"properties": {"schema": {"name": schema["tableName"], "columns": cols}}}
dce = {"location": location, "properties": {"networkAcls": {"publicNetworkAccess": "Enabled"}}}
dcr = {
    "location": location,
    "properties": {
        "dataCollectionEndpointId": dce_id,
        "streamDeclarations": {schema["streamName"]: {"columns": cols}},
        "destinations": {"logAnalytics": [{"workspaceResourceId": workspace_id, "name": "workspace"}]},
        "dataFlows": [{
            "streams": [schema["streamName"]],
            "destinations": ["workspace"],
            "transformKql": "source",
            "outputStream": schema["streamName"],
        }],
    },
}
for name, body in (("table", table), ("dce", dce), ("dcr", dcr)):
    json.dump(body, open(os.path.join(out, name + ".json"), "w"))
PY

echo "1/4 Table $TABLE"
az rest --method put --url "https://management.azure.com${WORKSPACE_ID}/tables/${TABLE}?api-version=2022-10-01" \
  --body "@$TMP/table.json" -o none

echo "2/4 Data Collection Endpoint $DCE_NAME"
DCE_ENDPOINT=$(az rest --method put --url "https://management.azure.com${DCE_ID}?api-version=2022-06-01" \
  --body "@$TMP/dce.json" --query properties.logsIngestion.endpoint -o tsv)

echo "3/4 Data Collection Rule $DCR_NAME"
DCR_IMMUTABLE_ID=$(az rest --method put --url "https://management.azure.com${DCR_ID}?api-version=2022-06-01" \
  --body "@$TMP/dcr.json" --query properties.immutableId -o tsv)

if [[ -n "$FUNCTION_APP_NAME" ]]; then
  echo "4/4 Granting $FUNCTION_APP_NAME access and setting app settings"
  PRINCIPAL_ID=$(az functionapp identity show -g "$RG" -n "$FUNCTION_APP_NAME" --query principalId -o tsv)
  az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
    --role "Monitoring Metrics Publisher" --scope "$DCR_ID" -o none
  az functionapp config appsettings set -g "$RG" -n "$FUNCTION_APP_NAME" -o none --settings \
    "DCE_ENDPOINT=$DCE_ENDPOINT" "DCR_IMMUTABLE_ID=$DCR_IMMUTABLE_ID" "DCR_STREAM_NAME=$STREAM"
else
  echo "4/4 Skipped (no function app name). Set these on the Function App and grant its identity"
  echo "    'Monitoring Metrics Publisher' on $DCR_ID:"
fi

echo "DCE_ENDPOINT=$DCE_ENDPOINT"
echo "DCR_IMMUTABLE_ID=$DCR_IMMUTABLE_ID"
echo "DCR_STREAM_NAME=$STREAM"
