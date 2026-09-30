# From Zero to Telemetry in Sentinel

## 1) Prerequisites

### Deployment assumptions for this PoC
- Azure region is fixed to **East US** (`eastus`).
- Azure Functions hosting is fixed to **Linux Consumption** (`Y1`). Microsoft is retiring Linux Consumption (Sept 2028); move to Flex Consumption before production.

- Azure CLI + Monitor extension
- go-sqlcmd (Entra authentication for `init-sql`)
- .NET SDK 8
- Azure Functions Core Tools v4
- Chrome browser / managed Chromebook test device

## 2) Deploy infrastructure
Creates Storage, Log Analytics + App Insights, Key Vault, a Linux Consumption Function App
(.NET 8 isolated), and Azure SQL, and prints the outputs (function app name, collector URL,
SQL server FQDN, Key Vault name).

**Azure SQL uses Microsoft Entra-only authentication** — there are no SQL logins or passwords.
- The SQL Entra admin defaults to the user running the script. A group is recommended: set
  `SQL_ENTRA_ADMIN_OBJECT_ID`, `SQL_ENTRA_ADMIN_NAME` and `SQL_ENTRA_ADMIN_TYPE=Group`. These are required
  when a service principal runs the deployment (GitHub Deploy workflow).
- The Function App connects with its managed identity
  (`Authentication=Active Directory Managed Identity` in `SQL_CONNECTION_STRING`, which contains no secret).
  Its database user is created by `init-sql` (step 2b).

**Key Vault** holds the HMAC key (`HmacKey1`). `HMAC_KEYS__KEY1` is a Key Vault reference resolved with the
app's managed identity (granted *Key Vault Secrets User*). The user running the script is granted
*Key Vault Secrets Officer* to read or rotate it (override with `KEYVAULT_ADMIN_OBJECT_ID`). To rotate, add a
new version (`az keyvault secret set --vault-name <kv> -n HmacKey1 --value <new>`); the reference is
versionless, so the app picks it up on its next refresh or restart.

The account running the deployment needs **Owner** or **User Access Administrator** on the resource group
(to create the Key Vault role assignments); in the GitHub Deploy workflow that is the OIDC service principal.
`AzureWebJobsStorage` remains a plain connection string: the zip deployment on Linux Consumption needs to
read it. Moving to Flex Consumption removes that key entirely (identity-based storage).

### Bash
```bash
az login
export HMAC_KEY_B64="$(openssl rand -base64 32)"   # keep this: it goes in the extension policy
bash infra/scripts/deploy-bicep.sh <resource-group> eastus
```

### PowerShell
```powershell
az login
pwsh infra/scripts/deploy-bicep.ps1 -ResourceGroup <rg> -Location eastus -HmacKeyB64 '<base64 key>'
```

## 2b) Create the schema and grant the Function App database access
Requires [go-sqlcmd](https://aka.ms/go-sqlcmd) (`winget install sqlcmd`, `brew install sqlcmd`) and `az login`
as the SQL Entra admin (or a member of the admin group). Allow your IP through the SQL firewall first:

```bash
az sql server firewall-rule create -g <rg> -s <sqlServerName> -n admin --start-ip-address <ip> --end-ip-address <ip>

SQL_SERVER=<sqlServerFqdn> SQL_DATABASE=<sqlDatabaseName> FUNCTION_APP_NAME=<functionAppName> \
  bash infra/scripts/init-sql.sh
```
```powershell
pwsh infra/scripts/init-sql.ps1 -SqlServer <sqlServerFqdn> -SqlDatabase <sqlDatabaseName> -FunctionAppName <functionAppName>
```

This applies `001`–`003` and runs `infra/scripts/sql/grant-function-identity.sql`, which creates a database
user for the Function App's managed identity with `db_datareader` and `db_datawriter`. Until this runs, the
collector returns 503 (SQL login fails) and devices keep their events queued. If the grant is run by a
service principal instead of a person, the SQL server needs an identity with the Entra *Directory Readers* role.

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
