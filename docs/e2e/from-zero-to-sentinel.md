# From Zero to Telemetry in Sentinel

## 1) Prerequisites

### Deployment assumptions for this PoC
- Azure region is fixed to **East US** (`eastus`).
- Azure Functions hosting is fixed to **Linux Consumption** (`Y1`). Microsoft is retiring Linux Consumption (Sept 2028); move to Flex Consumption before production.

- Azure CLI + Monitor extension
- .NET SDK 8
- Azure Functions Core Tools v4
- Chrome browser / managed Chromebook test device

## 2) Deploy infrastructure
Creates Storage, Log Analytics + App Insights, a Linux Consumption Function App (.NET 8 isolated),
and Azure SQL. The deployment sets `AzureWebJobsStorage`, `APPLICATIONINSIGHTS_CONNECTION_STRING`,
`SQL_CONNECTION_STRING` and `HMAC_KEYS__KEY1` on the Function App, and prints the outputs
(function app name, collector URL, SQL server FQDN).

### Bash
```bash
export SQL_ADMIN_PASSWORD='<strong password>'
export HMAC_KEY_B64="$(openssl rand -base64 32)"   # keep this: it goes in the extension policy
bash infra/scripts/deploy-bicep.sh <resource-group> eastus
```

### PowerShell
```powershell
pwsh infra/scripts/deploy-bicep.ps1 -ResourceGroup <rg> -Location eastus -SqlAdminPassword '<strong password>' -HmacKeyB64 '<base64 key>'
```

To run `init-sql`, add a SQL firewall rule for your own IP first
(`az sql server firewall-rule create -g <rg> -s <sqlServerName> -n admin --start-ip-address <ip> --end-ip-address <ip>`).

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

## 4) Configure Function app settings (Sentinel only)
The core settings are set by the deployment. Sentinel ingestion is optional and stays off until
these are set:
- `DCE_ENDPOINT`
- `DCR_IMMUTABLE_ID`
- `DCR_STREAM_NAME`

Grant Function App managed identity `Monitoring Metrics Publisher` on the DCR.
Note: the DCR stream columns in `create-dce-dcr.*` do not yet match the collector's Sentinel payload
(`PayloadNormalizer`); align them before enabling Sentinel.

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
