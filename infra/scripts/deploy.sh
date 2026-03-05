#!/usr/bin/env bash
set -euo pipefail

RG=${1:?resource group}
PREFIX=${2:?name prefix}
LOCATION=${3:-eastus}

if [[ "$LOCATION" != "eastus" ]]; then
  echo "This PoC is standardized on Azure region eastus. Override is not supported in this script."
  exit 1
fi

az group create -n "$RG" -l "$LOCATION" >/dev/null
az deployment group create \
  -g "$RG" \
  -f infra/bicep/main.bicep \
  -p prefix="$PREFIX" location="$LOCATION"
