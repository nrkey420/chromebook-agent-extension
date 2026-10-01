# Creates/updates the schema and grants the Function App's managed identity access.
# Uses Microsoft Entra authentication (the server has no SQL logins). Requires go-sqlcmd
# (winget install sqlcmd) and `az login` as the SQL Entra admin (or a member of the admin group).
# Your client IP must be allowed by the SQL server firewall.
param(
  [Parameter(Mandatory = $true)][string]$SqlServer,       # deployment output sqlServerFqdn
  [Parameter(Mandatory = $true)][string]$SqlDatabase,     # deployment output sqlDatabaseName
  [Parameter(Mandatory = $true)][string]$FunctionAppName, # deployment output functionAppName
  [switch]$SkipGrant                                       # schema only (see init-sql.sh)
)
$ErrorActionPreference = 'Stop'
$auth = @('--authentication-method', 'ActiveDirectoryDefault')
$files = @('collector/src/ChromeCollector.FunctionApp/Sql/001_tables.sql', 'collector/src/ChromeCollector.FunctionApp/Sql/002_views.sql', 'collector/src/ChromeCollector.FunctionApp/Sql/003_procedures.sql', 'collector/src/ChromeCollector.FunctionApp/Sql/004_security.sql')
foreach ($f in $files) {
  Write-Host "Applying $f"
  sqlcmd -S $SqlServer -d $SqlDatabase @auth -b -i $f
  if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed on $f" }
}
if ($SkipGrant) { Write-Host 'Skipping grant-function-identity.sql (-SkipGrant)'; return }
Write-Host "Granting database access to managed identity '$FunctionAppName'"
sqlcmd -S $SqlServer -d $SqlDatabase @auth -b -v "FunctionAppName=$FunctionAppName" -i infra/scripts/sql/grant-function-identity.sql
if ($LASTEXITCODE -ne 0) { throw 'sqlcmd failed on grant-function-identity.sql' }
