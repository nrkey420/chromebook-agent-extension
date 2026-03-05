# Chromebook Agent Extension (PoC)

This repository contains a Manifest V3 Chrome extension under `extension/` for collecting lightweight Chromebook telemetry:
- `NAVIGATION` events from browser history visits
- `DOWNLOAD` events from download lifecycle hooks
- `HEARTBEAT` events every 5 minutes

## Managed policy keys
The extension reads configuration from `chrome.storage.managed`:
- `collectorUrl` (string)
- `keyId` (string)
- `sharedSecret` (base64 string)
- `flushIntervalMs` (int, default `30000`)
- `batchSize` (int, default `50`)
- `debug` (boolean, default `false`)

## Permissions
The extension requests:
- `history`, `downloads`, `storage`, `tabs`, `identity`, `enterprise.deviceAttributes`

Host permissions are currently set to `https://*/*` to simplify PoC testing. **For production, restrict to the exact collector origin only**.

## Packaging
- Bash: `extension/tools/pack.sh`
- PowerShell: `extension/tools/pack.ps1`

Both scripts create a zip in `extension/dist/` for upload/testing.

## How to test locally
1. Prepare managed configuration in Chrome (or test build via temporary config policy tooling) with valid `collectorUrl`, `keyId`, and `sharedSecret`.
2. Load unpacked extension from `extension/` at `chrome://extensions` (Developer mode enabled).
3. Visit a few sites and trigger a sample download.
4. Confirm queue/flush behavior in the service worker console (`chrome://extensions` → extension → *Service worker*).
5. Optionally run packaging script and re-import the zip for distribution testing.
