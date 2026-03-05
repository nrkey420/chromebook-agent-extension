#!/usr/bin/env bash
set -euo pipefail

RG=${1:?resource group}
PREFIX=${2:?name prefix}
LOCATION=${3:-eastus}

az group create -n "$RG" -l "$LOCATION" >/dev/null
az deployment group create \
  -g "$RG" \
  -f infra/bicep/main.bicep \
  -p prefix="$PREFIX" location="$LOCATION"
