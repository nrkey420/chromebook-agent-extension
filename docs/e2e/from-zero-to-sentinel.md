# From Zero to Telemetry in Sentinel

## 1) Prerequisites

### Deployment assumptions for this PoC
- Azure region is fixed to **East US** (`eastus`).
- Azure Functions hosting is fixed to **Consumption** (`Y1`).

- Azure CLI + Monitor extension
- .NET SDK 8
- Azure Functions Core Tools v4
- Chrome browser / managed Chromebook test device

## 2) Deploy infrastructure
### Bash
```bash
bash infra/scripts/deploy.sh <resource-group> <prefix>
```

### PowerShell
```powershell
pwsh infra/scripts/deploy.ps1 -ResourceGroup <rg> -Prefix <prefix>
```

## 3) Create DCE/DCR for custom table stream
### Bash
```bash
bash infra/scripts/create-dce-dcr.sh <rg> eastus <workspaceResourceId>
```

### PowerShell
```powershell
pwsh infra/scripts/create-dce-dcr.ps1 -ResourceGroup <rg> -WorkspaceResourceId <workspaceResourceId>
```

Capture:
- DCE ingestion endpoint
- DCR immutable ID

## 4) Configure Function app settings
Set app settings in Azure:
- `HmacKeys__default`
- `HmacAllowedSkewSeconds`
- `RawContainerName`
- `SentinelEndpoint`
- `SentinelDcrImmutableId`
- `SentinelStreamName`

Grant Function App managed identity `Monitoring Metrics Publisher` (or relevant Logs Ingestion permission) on the DCR.

## 5) Deploy function code
### Bash
```bash
bash infra/scripts/deploy-function.sh <function-app-name> <resource-group>
```

### PowerShell
```powershell
pwsh infra/scripts/deploy-function.ps1 -FunctionApp <function-app-name> -ResourceGroup <resource-group>
```

## 6) Package and deploy extension
```bash
bash extension/tools/pack.sh
```
Use Google Admin force-install and apply managed policy JSON with collector endpoint + HMAC key metadata.

## 7) Validate end-to-end
1. Confirm extension queues and flushes in service worker logs.
2. Confirm Function logs show accepted events.
3. Confirm Blob container `raw-events` receives JSONL files.
4. Run KQL:

```kusto
ChromebookActivity_CL
| where TimeGenerated > ago(30m)
| order by TimeGenerated desc
```
