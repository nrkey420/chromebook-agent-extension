# Creates (or updates) the Sentinel pipeline for collector events and wires it to the Function App:
#   1. custom table ChromebookActivity_CL in the Log Analytics / Sentinel workspace
#   2. Data Collection Endpoint (DCE)
#   3. Data Collection Rule (DCR) whose stream matches the collector payload
#   4. (with -FunctionAppName) grants the app's managed identity "Monitoring Metrics Publisher" on the DCR
#      and sets DCE_ENDPOINT / DCR_IMMUTABLE_ID / DCR_STREAM_NAME on the app
# Columns come from infra/sentinel/chromebook-activity-schema.json (kept in sync with PayloadNormalizer by tests).
# All calls are PUTs, so re-running is safe. -Location must be the workspace's region.
param(
  [Parameter(Mandatory = $true)][string]$ResourceGroup,
  [Parameter(Mandatory = $true)][string]$Location,
  [Parameter(Mandatory = $true)][string]$WorkspaceResourceId,
  [string]$FunctionAppName = '',
  [string]$DceName = 'chromebook-dce',
  [string]$DcrName = 'chromebook-dcr'
)
$ErrorActionPreference = 'Stop'

function Invoke-Az {
  $output = & az @args
  if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed." }
  return $output
}

$schema = Get-Content (Join-Path $PSScriptRoot '../sentinel/chromebook-activity-schema.json') -Raw | ConvertFrom-Json
$sub = Invoke-Az account show --query id -o tsv
$dceId = "/subscriptions/$sub/resourceGroups/$ResourceGroup/providers/Microsoft.Insights/dataCollectionEndpoints/$DceName"
$dcrId = "/subscriptions/$sub/resourceGroups/$ResourceGroup/providers/Microsoft.Insights/dataCollectionRules/$DcrName"

$table = @{ properties = @{ schema = @{ name = $schema.tableName; columns = $schema.columns } } }
$dce = @{ location = $Location; properties = @{ networkAcls = @{ publicNetworkAccess = 'Enabled' } } }
$dcr = @{
  location   = $Location
  properties = @{
    dataCollectionEndpointId = $dceId
    streamDeclarations       = @{ $schema.streamName = @{ columns = $schema.columns } }
    destinations             = @{ logAnalytics = @(@{ workspaceResourceId = $WorkspaceResourceId; name = 'workspace' }) }
    dataFlows                = @(@{
        streams      = @($schema.streamName)
        destinations = @('workspace')
        transformKql = 'source'
        outputStream = $schema.streamName
      })
  }
}

$tmp = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid()))
try {
  $table | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $tmp 'table.json')
  $dce | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $tmp 'dce.json')
  $dcr | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $tmp 'dcr.json')

  Write-Host "1/4 Table $($schema.tableName)"
  Invoke-Az rest --method put --url "https://management.azure.com$WorkspaceResourceId/tables/$($schema.tableName)?api-version=2022-10-01" `
    --body "@$(Join-Path $tmp 'table.json')" -o none | Out-Null

  Write-Host "2/4 Data Collection Endpoint $DceName"
  $dceEndpoint = Invoke-Az rest --method put --url "https://management.azure.com$($dceId)?api-version=2022-06-01" `
    --body "@$(Join-Path $tmp 'dce.json')" --query properties.logsIngestion.endpoint -o tsv

  Write-Host "3/4 Data Collection Rule $DcrName"
  $dcrImmutableId = Invoke-Az rest --method put --url "https://management.azure.com$($dcrId)?api-version=2022-06-01" `
    --body "@$(Join-Path $tmp 'dcr.json')" --query properties.immutableId -o tsv
}
finally {
  Remove-Item $tmp -Recurse -Force
}

if ($FunctionAppName) {
  Write-Host "4/4 Granting $FunctionAppName access and setting app settings"
  $principalId = Invoke-Az functionapp identity show -g $ResourceGroup -n $FunctionAppName --query principalId -o tsv
  Invoke-Az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
    --role 'Monitoring Metrics Publisher' --scope $dcrId -o none | Out-Null
  Invoke-Az functionapp config appsettings set -g $ResourceGroup -n $FunctionAppName -o none --settings `
    "DCE_ENDPOINT=$dceEndpoint" "DCR_IMMUTABLE_ID=$dcrImmutableId" "DCR_STREAM_NAME=$($schema.streamName)" | Out-Null
}
else {
  Write-Host "4/4 Skipped (no -FunctionAppName). Set these on the Function App and grant its identity"
  Write-Host "    'Monitoring Metrics Publisher' on $dcrId"
}

Write-Host "DCE_ENDPOINT=$dceEndpoint"
Write-Host "DCR_IMMUTABLE_ID=$dcrImmutableId"
Write-Host "DCR_STREAM_NAME=$($schema.streamName)"
