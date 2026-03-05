# Operations

## Deployment assumptions (PoC)
- Azure region: **East US** (`eastus`).
- Azure Functions hosting plan: **Consumption** (`Y1`).
- Premium plans, APIM, Front Door, and private endpoints are out of scope for this PoC; consider them only as future hardening options.

## Build and test
### Extension package
- bash: `bash extension/tools/pack.sh`
- PowerShell: `pwsh extension/tools/pack.ps1`

### Collector unit tests
- `dotnet test collector/ChromebookCollector.sln`

## Local collector smoke test (curl)
1. Copy template and set values:
   - `cp collector/src/ChromebookCollector/local.settings.json.template collector/src/ChromebookCollector/local.settings.json`
   - set `HmacKeys__default` and storage values.
2. Start host:
   - `func start --python` is not needed; for .NET isolated use `func start` in `collector/src/ChromebookCollector`.
3. Generate signed request and submit:

```bash
BODY='{"events":[{"type":"HEARTBEAT","observedAt":"2026-01-01T00:00:00Z","payload":{},"device":{}}]}'
TS=$(date +%s)
SECRET_B64='<base64-secret>'
SIG=$(python3 - <<'PY'
import base64, hmac, hashlib, os
body=os.environ['BODY']
ts=os.environ['TS']
key=base64.b64decode(os.environ['SECRET_B64'])
print(base64.b64encode(hmac.new(key, f"{ts}\n{body}".encode(), hashlib.sha256).digest()).decode())
PY
)

curl -i -X POST "http://localhost:7071/api/v1/chrome/events/batch" \
  -H "Content-Type: application/json" \
  -H "X-Key-Id: default" \
  -H "X-Timestamp: $TS" \
  -H "X-Signature: $SIG" \
  -H "X-Client: chromebook-extension" \
  -d "$BODY"
```

Expected: `HTTP/1.1 202 Accepted`.

## Sentinel verification KQL
```kusto
ChromebookActivity_CL
| where TimeGenerated > ago(1h)
| summarize count() by EventType_s
```

```kusto
ChromebookActivity_CL
| where EventType_s == "DOWNLOAD"
| project TimeGenerated, UserEmail_s, DirectoryDeviceId_s, DownloadState_s, DownloadDanger_s, PayloadJson_s
| order by TimeGenerated desc
```

## End-to-end verification guide (Azure deployment)
1. **Deploy infrastructure**
   - Deploy collector + storage + monitoring resources using your IaC pipeline or deployment scripts.
   - Confirm the Function App is running and reachable.

2. **Set Function App setting `HMAC_KEYS__KEY1`**
   - Add/update the app setting in Azure Portal (`Function App` → `Settings` → `Environment variables`) or CLI:
   - `az functionapp config appsettings set --name <function-app-name> --resource-group <rg> --settings HMAC_KEYS__KEY1=<base64-secret>`
   - Restart the Function App if required.

3. **Confirm health endpoint**
   - `curl -i https://<collector-host>/api/health`
   - Expected: `HTTP 200` with `ok` response body.

4. **Submit a signed test batch and verify `202` + blob write**
   - Use a signed request (same pattern as local smoke test), targeting production host:

```bash
BODY='{"events":[{"type":"HEARTBEAT","observedAt":"2026-01-01T00:00:00Z","payload":{},"device":{"deviceId":"test-device-1","userEmail":"analyst@example.com"}}]}'
TS=$(date +%s)
SECRET_B64='<base64-secret>'
SIG=$(python3 - <<'PY'
import base64, hmac, hashlib, os
body=os.environ['BODY']
ts=os.environ['TS']
key=base64.b64decode(os.environ['SECRET_B64'])
print(base64.b64encode(hmac.new(key, f"{ts}\n{body}".encode(), hashlib.sha256).digest()).decode())
PY
)

curl -i -X POST "https://<collector-host>/api/v1/chrome/events/batch" \
  -H "Content-Type: application/json" \
  -H "X-Key-Id: key1" \
  -H "X-Timestamp: $TS" \
  -H "X-Signature: $SIG" \
  -H "X-Client: chromebook-extension" \
  -d "$BODY"
```

   - Expected: `HTTP/1.1 202 Accepted`.
   - Verify raw blob write in the configured storage account/container (Portal or Storage Explorer) and confirm a new event blob exists with a recent timestamp.

5. **Configure DCE/DCR and confirm Sentinel ingestion**
   - Set these Function App settings:
     - `DCE_ENDPOINT`
     - `DCR_IMMUTABLE_ID`
     - `DCR_STREAM_NAME`
   - Re-send the signed batch after settings are applied.
   - Validate ingestion in Log Analytics / Sentinel:

```kusto
ChromebookActivity_CL
| where TimeGenerated > ago(15m)
| where DirectoryDeviceId_s == "test-device-1"
| order by TimeGenerated desc
```

   - Expected: newly ingested row(s) for the test device in near-real time.

6. **Install extension in test Chromebook OU and validate pipeline**
   - Push extension policy to a non-production OU with managed storage values (`collectorUrl`, `keyId`, `sharedSecret`, optional `debug`).
   - On a test Chromebook, generate activity (browse pages, perform download, wait for heartbeat).
   - Verify extension posts batched events (service worker logs / collector request logs).
   - Confirm queue drains after transient failure recovery (stop collector briefly, restore, observe backlog flush).

7. **KQL query pack for analyst validation**
   - **Recently visited domains by user**

```kusto
ChromebookActivity_CL
| where TimeGenerated > ago(24h)
| where EventType_s == "NAVIGATION"
| summarize LastSeen=max(TimeGenerated), Visits=count() by UserEmail_s, Domain_s
| order by LastSeen desc
```

   - **Downloads marked dangerous**

```kusto
ChromebookActivity_CL
| where TimeGenerated > ago(7d)
| where EventType_s == "DOWNLOAD"
| where tolower(DownloadDanger_s) !in ("", "safe", "not_dangerous")
| project TimeGenerated, UserEmail_s, DirectoryDeviceId_s, DownloadUrl_s, DownloadFilename_s, DownloadDanger_s, DownloadState_s
| order by TimeGenerated desc
```

   - **Newly seen domains in last 24 hours**

```kusto
let baseline = ChromebookActivity_CL
| where EventType_s == "NAVIGATION"
| where TimeGenerated between (ago(30d) .. ago(24h))
| distinct Domain_s;
ChromebookActivity_CL
| where EventType_s == "NAVIGATION"
| where TimeGenerated > ago(24h)
| where isnotempty(Domain_s)
| where Domain_s !in (baseline)
| summarize FirstSeen=min(TimeGenerated), Users=dcount(UserEmail_s), Visits=count() by Domain_s
| order by FirstSeen desc
```

   - **Activity timeline for a specific `deviceId`**

```kusto
ChromebookActivity_CL
| where TimeGenerated > ago(24h)
| where DirectoryDeviceId_s == "<device-id>"
| project TimeGenerated, EventType_s, UserEmail_s, Domain_s, DownloadFilename_s, DownloadState_s
| order by TimeGenerated asc
```

   - **Correlation by `userEmail` across devices**

```kusto
ChromebookActivity_CL
| where TimeGenerated > ago(7d)
| where UserEmail_s == "<user@domain>"
| summarize Events=count(), DistinctDomains=dcount(Domain_s), LastSeen=max(TimeGenerated) by DirectoryDeviceId_s
| order by LastSeen desc
```
## Local validation checklist
1. Configure managed storage with `collectorUrl`, `keyId`, `sharedSecret`, and optional `debug=true`.
2. Load the unpacked extension (`extension/`) in Chrome Developer Mode.
3. Generate telemetry:
   - Navigate to several pages (`NAVIGATION`)
   - Start/complete/cancel a file download (`DOWNLOAD`)
   - Wait for heartbeat interval or reload extension to force startup heartbeat (`HEARTBEAT`)
4. Open service worker logs and verify batches are signed and posted.
5. Stop collector temporarily to confirm retries (at-least-once semantics with exponential backoff), then restore collector and verify backlog drains.

## Production hardening reminder
The manifest currently allows `https://*/*` host access for PoC flexibility. Before production rollout, pin `host_permissions` to your collector URL origin.

## GitHub Actions deployment and secret management

### Required repository secrets
Configure the following repository secrets for workflow-based deployment:

- `AZURE_FUNCTIONAPP_NAME`
- `AZURE_RESOURCE_GROUP`
- `HMAC_KEYS__KEY1`
- `DCE_ENDPOINT`
- `DCR_IMMUTABLE_ID`
- `DCR_STREAM_NAME`

For authentication, prefer OIDC and configure:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

If OIDC is not yet configured, use publish profile fallback:

- `AZURE_FUNCTIONAPP_PUBLISH_PROFILE`

### HMAC key rotation runbook
1. Generate a new secret key in your secret manager/HSM and assign it to a new key ID/version in the collector configuration.
2. Update producer-side configuration so clients can sign with the new key (and keep old key accepted during overlap).
3. In GitHub repository settings (`Settings` → `Secrets and variables` → `Actions`), update `HMAC_KEYS__KEY1` to the new key material.
4. Trigger `Deploy Collector` workflow (or merge to `main`) to apply updated app settings.
5. Validate ingestion and signature verification.
6. After traffic is fully migrated, retire the old key from the collector.

### Updating GitHub secrets safely
1. Update changed values in repository Action secrets.
2. Re-run deployment to push new app settings to Azure Function App.
3. Confirm effective settings via Azure Portal or `az functionapp config appsettings list`.
4. Record change window and rollback data in your operational log.
