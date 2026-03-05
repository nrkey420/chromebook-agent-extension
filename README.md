# Chromebook Activity PoC

End-to-end proof of concept for collecting Chromebook browser activity and ingesting into Microsoft Sentinel.

## Repository layout
- `extension/`: Manifest V3 Chrome extension.
- `collector/`: Azure Functions (.NET 8 isolated) collector.
- `infra/`: Bicep and deployment scripts.
- `.github/workflows/`: CI/CD for build and deployment.
- `docs/`: architecture, operations, and end-to-end validation.

## Extension capabilities
- Captures:
  - `NAVIGATION` (`chrome.history.onVisited`)
  - `DOWNLOAD` (`chrome.downloads.onCreated`, `onChanged`)
  - `HEARTBEAT` every 5 minutes
- Reads managed policy config from `chrome.storage.managed`.
- Queues events in `chrome.storage.local`.
- Flushes batches with exponential backoff retry.
- Signs payloads using HMAC-SHA256 (`timestamp + "\n" + body`).

## Managed policy keys
Managed values expected by extension:
- `collectorUrl` (string)
- `keyId` (string)
- `sharedSecret` (base64)
- `flushIntervalMs` (int, default `30000`)
- `batchSize` (int, default `50`)
- `collectTitles` (bool, default `true`)
- `debug` (bool, default `false`)

Sample policy object:

```json
{
  "collectorUrl": "https://<function-app>.azurewebsites.net",
  "keyId": "default",
  "sharedSecret": "<base64-secret>",
  "flushIntervalMs": 30000,
  "batchSize": 50,
  "collectTitles": true,
  "debug": true
}
```

## Google Admin force-install + managed config
1. Upload extension package (`extension/dist/*.zip`) to your trusted distribution path or use self-hosted update URL.
2. In **Google Admin Console** → **Devices** → **Chrome** → **Apps & extensions** → **Users & browsers**:
   - add extension by ID
   - set install mode to **Force install**
3. Set **Policy for extensions** / managed configuration JSON using the keys above.
4. Verify on target Chromebook at `chrome://policy` and `chrome://extensions`.

## Collector API contract
Endpoint: `POST /api/v1/chrome/events/batch`

Required headers:
- `X-Key-Id`
- `X-Timestamp` (unix seconds)
- `X-Signature` (base64 HMAC-SHA256)
- `X-Client`

Body:
```json
{
  "events": [
    {
      "type": "NAVIGATION",
      "observedAt": "2026-01-01T00:00:00Z",
      "payload": { "url": "https://example.com" },
      "device": { "directoryDeviceId": "abc", "serialNumber": "xyz", "userEmail": "user@contoso.com" }
    }
  ]
}
```

On success collector returns `202 Accepted`, writes raw JSONL to Blob Storage, and ingests normalized records via Logs Ingestion API.


## PoC infrastructure assumptions
- Azure region: **East US** (`eastus`).
- Azure Functions plan: **Consumption** (`Y1`).
- Premium plans, APIM, Front Door, and private endpoints are out of scope for the PoC and should be treated as future hardening options only.

## Quickstart
- Architecture: `docs/architecture.md`
- Operations checklist: `docs/operations.md`
- Privacy guardrails: `docs/privacy-guardrails.md`
- Full runbook: `docs/e2e/from-zero-to-sentinel.md`
- Milestone plan: `IMPLEMENT.md`
