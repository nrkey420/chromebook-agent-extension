# Deploys infra/bicep/main.bicep.
# Required: -SqlAdminPassword and -HmacKeyB64 (or env vars SQL_ADMIN_PASSWORD / HMAC_KEY_B64).
# Generate an HMAC key: [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
# (the same value goes in the extension's managed policy)
param(
  [string]$ResourceGroup = 'rg-chromebook-poc',
  [string]$Location = 'eastus',
  [string]$SqlAdminPassword = $env:SQL_ADMIN_PASSWORD,
  [string]$HmacKeyB64 = $env:HMAC_KEY_B64
)
$ErrorActionPreference = 'Stop'
if (-not $SqlAdminPassword) { throw 'Provide -SqlAdminPassword or set SQL_ADMIN_PASSWORD.' }
if (-not $HmacKeyB64) { throw 'Provide -HmacKeyB64 or set HMAC_KEY_B64.' }
az group create -n $ResourceGroup -l $Location | Out-Null
az deployment group create -g $ResourceGroup -f infra/bicep/main.bicep `
  -p '@infra/bicep/main.parameters.json' `
  -p location=$Location sqlAdminPassword=$SqlAdminPassword hmacKey=$HmacKeyB64 `
  --query properties.outputs -o json
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed.' }
