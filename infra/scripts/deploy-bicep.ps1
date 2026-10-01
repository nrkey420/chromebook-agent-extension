# Deploys infra/bicep/main.bicep.
# Required: -HmacKeyB64 (or env HMAC_KEY_B64); stored in Key Vault, and the same value goes in the extension policy.
# Generate one: [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
# Optional (default to the signed-in `az` user; required when running as a service principal):
#   -SqlEntraAdminObjectId / -SqlEntraAdminName / -SqlEntraAdminType (User|Group|Application)
#   -KeyVaultAdminObjectId (granted Key Vault Secrets Officer)
# Optional Google sync settings (docs/google-sync.md); the sync stays off without -GoogleAdminEmail:
#   -GoogleAdminEmail / -GoogleDeviceOrgUnit / -GoogleStudentIdSource (or env GOOGLE_ADMIN_EMAIL, ...)
param(
  [string]$ResourceGroup = 'rg-chromebook-poc',
  [string]$Location = 'eastus',
  [string]$HmacKeyB64 = $env:HMAC_KEY_B64,
  [string]$SqlEntraAdminObjectId = $env:SQL_ENTRA_ADMIN_OBJECT_ID,
  [string]$SqlEntraAdminName = $env:SQL_ENTRA_ADMIN_NAME,
  [string]$SqlEntraAdminType = $(if ($env:SQL_ENTRA_ADMIN_TYPE) { $env:SQL_ENTRA_ADMIN_TYPE } else { 'User' }),
  [string]$KeyVaultAdminObjectId = $env:KEYVAULT_ADMIN_OBJECT_ID,
  [string]$GoogleAdminEmail = $env:GOOGLE_ADMIN_EMAIL,
  [string]$GoogleDeviceOrgUnit = $env:GOOGLE_DEVICE_ORG_UNIT,
  [string]$GoogleStudentIdSource = $env:GOOGLE_STUDENT_ID_SOURCE
)
$ErrorActionPreference = 'Stop'
if (-not $HmacKeyB64) { throw 'Provide -HmacKeyB64 or set HMAC_KEY_B64.' }

if (-not $SqlEntraAdminObjectId -or -not $KeyVaultAdminObjectId) {
  $me = az ad signed-in-user show --query '{id:id, upn:userPrincipalName}' -o json 2>$null | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { $me = $null }
  if ($me) {
    if (-not $SqlEntraAdminObjectId) { $SqlEntraAdminObjectId = $me.id; if (-not $SqlEntraAdminName) { $SqlEntraAdminName = $me.upn } }
    if (-not $KeyVaultAdminObjectId) { $KeyVaultAdminObjectId = $me.id }
  }
}
if (-not $SqlEntraAdminObjectId -or -not $SqlEntraAdminName) {
  throw 'Provide -SqlEntraAdminObjectId and -SqlEntraAdminName (could not read the signed-in user).'
}

$params = @(
  "location=$Location", "hmacKey=$HmacKeyB64",
  "sqlEntraAdminObjectId=$SqlEntraAdminObjectId", "sqlEntraAdminName=$SqlEntraAdminName", "sqlEntraAdminPrincipalType=$SqlEntraAdminType"
)
if ($KeyVaultAdminObjectId) { $params += "keyVaultAdminObjectId=$KeyVaultAdminObjectId" }
# Always passed, so a redeploy keeps the Google settings in step with what the caller provides.
$params += "googleAdminEmail=$GoogleAdminEmail", "googleDeviceOrgUnit=$GoogleDeviceOrgUnit"
if ($GoogleStudentIdSource) { $params += "googleStudentIdSource=$GoogleStudentIdSource" }
az group create -n $ResourceGroup -l $Location | Out-Null
az deployment group create -g $ResourceGroup -f infra/bicep/main.bicep `
  -p '@infra/bicep/main.parameters.json' `
  -p @params `
  --query properties.outputs -o json
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed.' }
