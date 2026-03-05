#!/usr/bin/env bash
set -euo pipefail
FUNC_NAME=${1:?function app name}
pushd collector/src/ChromebookCollector >/dev/null
dotnet publish -c Release -o publish
cd publish
zip -r ../../../../collector.zip . >/dev/null
popd >/dev/null
az functionapp deployment source config-zip -g ${2:?resource group} -n "$FUNC_NAME" --src collector.zip
