# Adds Microsoft Entra groups to the Chromebook database roles (docs/sql-access.md).
# Run as a person who is the SQL Entra admin (or in the admin group), after the schema has been applied.
# Requires go-sqlcmd (winget install sqlcmd) and `az login`; your client IP must be allowed by the SQL server firewall.
# Pass the Entra groups' display names; at least one.
param(
  [Parameter(Mandatory = $true)][string]$SqlServer,   # deployment output sqlServerFqdn
  [Parameter(Mandatory = $true)][string]$SqlDatabase, # deployment output sqlDatabaseName
  [string]$DeviceReadersGroup = '',                   # helpdesk / ops
  [string]$InvestigatorsGroup = '',                   # IR analysts
  [string]$AuditReviewersGroup = ''                   # IR lead / compliance
)
$ErrorActionPreference = 'Stop'
if (-not ($DeviceReadersGroup + $InvestigatorsGroup + $AuditReviewersGroup)) {
  throw 'Pass at least one of -DeviceReadersGroup, -InvestigatorsGroup, -AuditReviewersGroup.'
}
# The names go into an N'...' literal: double any single quote.
function Q([string]$s) { $s.Replace("'", "''") }
sqlcmd -S $SqlServer -d $SqlDatabase --authentication-method ActiveDirectoryDefault -b `
  -v "DeviceReadersGroup=$(Q $DeviceReadersGroup)" "InvestigatorsGroup=$(Q $InvestigatorsGroup)" "AuditReviewersGroup=$(Q $AuditReviewersGroup)" `
  -i infra/scripts/sql/grant-roles.sql
if ($LASTEXITCODE -ne 0) { throw 'sqlcmd failed on grant-roles.sql' }
