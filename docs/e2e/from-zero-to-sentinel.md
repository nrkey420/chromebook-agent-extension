# From Zero to Telemetry in Sentinel

## 1) Prerequisites

### Deployment assumptions for this PoC
- Azure region is fixed to **East US** (`eastus`).
- Azure Functions hosting is **Flex Consumption** (`FC1`, Linux, .NET 8 isolated). Scale-out is capped by the
  `maximumInstanceCount` parameter (default 40); memory per instance by `instanceMemoryMB` (default 2048).

- Azure CLI + Monitor extension
- go-sqlcmd (Entra authentication for `init-sql`)
- .NET SDK 8
- Azure Functions Core Tools v4
- Chrome browser / managed Chromebook test device

## 2) Deploy infrastructure
Creates Storage, Log Analytics + App Insights, Key Vault, a Flex Consumption Function App
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
**Storage uses no keys either.** Shared-key access is disabled on the storage account. The Function App uses
its managed identity (`AzureWebJobsStorage__accountName`, granted *Storage Blob Data Owner*) for the Functions
host, for Flex deployment packages (container `app-package-<app name>`), and for the raw event archive
(`chrome-activity-raw`). People who need to browse blobs need a data role on the account (for example
*Storage Blob Data Reader*); storage account keys and connection strings will not work.

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
Use a recent Azure CLI (`az upgrade`) so `config-zip` supports Flex Consumption. On Flex, the package is
uploaded into the app's deployment container using the app's managed identity. Right after the first infra
deployment, the storage and Key Vault role assignments can take a few minutes to take effect: if the zip
deployment fails with an authorization error, or the portal shows a Key Vault reference error, wait a few
minutes and re-run the deployment (which also restarts the app and re-resolves the references).
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
