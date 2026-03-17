#!/usr/bin/env bash
set -euo pipefail
RG=${1:-rg-chromebook-poc}
LOC=${2:-eastus}
az group create -n "$RG" -l "$LOC" >/dev/null
az deployment group create -g "$RG" -f infra/bicep/main.bicep -p @infra/bicep/main.parameters.json -o table
