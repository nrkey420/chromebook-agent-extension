#!/usr/bin/env bash
# Packages the extension into dist/chromebook-activity-extension-v<version>.zip.
# Optional: COLLECTOR_HOST_PERMISSION overrides the collector host permission in the packaged manifest
# (default https://*.azurewebsites.net/*), e.g. for a custom domain: COLLECTOR_HOST_PERMISSION='https://collector.district.org/*'
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$EXT_DIR/dist"
STAGING="$OUT_DIR/_staging"
mkdir -p "$OUT_DIR"
rm -rf "$STAGING"
mkdir -p "$STAGING"

cp "$EXT_DIR/manifest.json" "$EXT_DIR/sw.js" "$STAGING/"
cp -R "$EXT_DIR/src" "$EXT_DIR/policy" "$STAGING/"

if [[ -n "${COLLECTOR_HOST_PERMISSION:-}" ]]; then
  python3 - "$STAGING/manifest.json" "$COLLECTOR_HOST_PERMISSION" <<'PY'
import json, sys
path, perm = sys.argv[1], sys.argv[2]
m = json.load(open(path))
m["host_permissions"] = [perm]
json.dump(m, open(path, "w"), indent=2)
PY
fi

VERSION="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['version'])" "$STAGING/manifest.json")"
ZIP_PATH="$OUT_DIR/chromebook-activity-extension-v${VERSION}.zip"
rm -f "$ZIP_PATH"
(cd "$STAGING" && zip -qr "$ZIP_PATH" .)
rm -rf "$STAGING"

echo "Created $ZIP_PATH"
