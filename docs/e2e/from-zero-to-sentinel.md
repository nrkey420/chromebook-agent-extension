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
Creates Storage, Log Analytics + App Insights, Key Vault, a Linux Consumption Function App
(.NET 8 isolated), and Azure SQL, and prints the outputs (function app name, collector URL,
SQL server FQDN, Key Vault name).

Secrets live in Key Vault (`SqlConnectionString`, `SqlAdminPassword`, `HmacKey1`). The Function App's
`SQL_CONNECTION_STRING` and `HMAC_KEYS__KEY1` settings are Key Vault references, resolved with the app's
managed identity (granted *Key Vault Secrets User*). The signed-in user running the script is granted
*Key Vault Secrets Officer* so they can read or rotate secrets (override with `KEYVAULT_ADMIN_OBJECT_ID`).
The account running the deployment needs **Owner** or **User Access Administrator** on the resource group
(to create the Key Vault role assignments); in the GitHub Deploy workflow that is the OIDC service principal.
`AzureWebJobsStorage` remains a plain connection string: the zip deployment on Linux Consumption needs to
read it. Moving to Flex Consumption removes that key entirely (identity-based storage).

To rotate a secret, add a new version in Key Vault (`az keyvault secret set --vault-name <kv> -n HmacKey1 --value <new>`);
the references are versionless, so the app picks it up on its next refresh or restart.

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
Deploying code also restarts the app, which re-resolves the Key Vault references. If you redeploy only
infrastructure and the portal shows a Key Vault reference error (for example right after the role
assignment was created), restart the Function App.
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
