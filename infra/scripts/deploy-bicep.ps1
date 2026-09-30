# Deploys infra/bicep/main.bicep.
# Required: -SqlAdminPassword and -HmacKeyB64 (or env vars SQL_ADMIN_PASSWORD / HMAC_KEY_B64).
# Generate an HMAC key: [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
# (the same value goes in the extension's managed policy)
# Optional: -KeyVaultAdminObjectId (defaults to the signed-in user) gets Key Vault Secrets Officer.
# Secrets are stored in Key Vault; the Function App reads them via Key Vault references.
param(
  [string]$ResourceGroup = 'rg-chromebook-poc',
  [string]$Location = 'eastus',
  [string]$SqlAdminPassword = $env:SQL_ADMIN_PASSWORD,
  [string]$HmacKeyB64 = $env:HMAC_KEY_B64,
  [string]$KeyVaultAdminObjectId = $env:KEYVAULT_ADMIN_OBJECT_ID
)
$ErrorActionPreference = 'Stop'
if (-not $SqlAdminPassword) { throw 'Provide -SqlAdminPassword or set SQL_ADMIN_PASSWORD.' }
if (-not $HmacKeyB64) { throw 'Provide -HmacKeyB64 or set HMAC_KEY_B64.' }
if (-not $KeyVaultAdminObjectId) {
  $KeyVaultAdminObjectId = az ad signed-in-user show --query id -o tsv 2>$null
  if ($LASTEXITCODE -ne 0) { $KeyVaultAdminObjectId = '' }
}
$params = @("location=$Location", "sqlAdminPassword=$SqlAdminPassword", "hmacKey=$HmacKeyB64")
if ($KeyVaultAdminObjectId) { $params += "keyVaultAdminObjectId=$KeyVaultAdminObjectId" }
az group create -n $ResourceGroup -l $Location | Out-Null
az deployment group create -g $ResourceGroup -f infra/bicep/main.bicep `
  -p '@infra/bicep/main.parameters.json' `
  -p @params `
  --query properties.outputs -o json
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed.' }
