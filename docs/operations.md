# Operations

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
