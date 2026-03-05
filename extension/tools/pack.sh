#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="$EXT_DIR/dist"
mkdir -p "$OUT_DIR"

VERSION="$(python3 -c "import json;print(json.load(open('$EXT_DIR/manifest.json'))['version'])")"
ZIP_PATH="$OUT_DIR/chromebook-activity-extension-v${VERSION}.zip"

rm -f "$ZIP_PATH"
(
  cd "$EXT_DIR"
  zip -r "$ZIP_PATH" . -x "tools/*" "dist/*" "*.DS_Store"
)

echo "Created $ZIP_PATH"
