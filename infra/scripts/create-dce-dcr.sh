#!/usr/bin/env bash
set -euo pipefail

RG=${1:?resource group}
LOCATION=${2:-eastus}
WORKSPACE_ID=${3:?log analytics workspace resource id}
DCE_NAME=${4:-chromebook-dce}
DCR_NAME=${5:-chromebook-dcr}

DCE_ID=$(az monitor data-collection endpoint create \
  -g "$RG" -n "$DCE_NAME" -l "$LOCATION" \
  --public-network-access Enabled \
  --query id -o tsv)

STREAM_DECL='{"streamDeclarations":{"Custom-ChromebookActivity_CL":{"columns":[{"name":"TimeGenerated","type":"datetime"},{"name":"EventType","type":"string"},{"name":"Url","type":"string"},{"name":"Title","type":"string"},{"name":"DownloadState","type":"string"},{"name":"DownloadDanger","type":"string"},{"name":"UserEmail","type":"string"},{"name":"DirectoryDeviceId","type":"string"},{"name":"PayloadJson","type":"string"},{"name":"Source","type":"string"}]}}}'

az monitor data-collection rule create \
  -g "$RG" -n "$DCR_NAME" -l "$LOCATION" \
  --data-collection-endpoint-id "$DCE_ID" \
  --rule-file <(cat <<JSON
{
  "properties": {
    "streamDeclarations": {
      "Custom-ChromebookActivity_CL": {
        "columns": [
          {"name":"TimeGenerated","type":"datetime"},
          {"name":"EventType","type":"string"},
          {"name":"Url","type":"string"},
          {"name":"Title","type":"string"},
          {"name":"DownloadState","type":"string"},
          {"name":"DownloadDanger","type":"string"},
          {"name":"UserEmail","type":"string"},
          {"name":"DirectoryDeviceId","type":"string"},
          {"name":"PayloadJson","type":"string"},
          {"name":"Source","type":"string"}
        ]
      }
    },
    "destinations": {
      "logAnalytics": [
        {
          "workspaceResourceId": "$WORKSPACE_ID",
          "name": "la"
        }
      ]
    },
    "dataFlows": [
      {
        "streams": ["Custom-ChromebookActivity_CL"],
        "destinations": ["la"],
        "outputStream": "Custom-ChromebookActivity_CL"
      }
    ]
  }
}
JSON
)
