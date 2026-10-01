#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)" # repo root, so the script works from any folder
FUNC_NAME=${1:?function app name}
pushd "$ROOT/collector/src/ChromeCollector.FunctionApp" >/dev/null
dotnet publish -c Release -o publish
cd publish
zip -r "$ROOT/collector.zip" . >/dev/null
popd >/dev/null
az functionapp deployment source config-zip -g "${2:?resource group}" -n "$FUNC_NAME" --src "$ROOT/collector.zip"
