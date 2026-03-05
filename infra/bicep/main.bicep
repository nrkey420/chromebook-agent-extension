@description('PoC deployment region. Defaults to East US.')
param location string = 'eastus'
param prefix string
param storageSku string = 'Standard_LRS'

var storageName = toLower('${prefix}st${uniqueString(resourceGroup().id)}')
var planName = '${prefix}-plan'
var appInsightsName = '${prefix}-appi'
var functionName = '${prefix}-func-${uniqueString(resourceGroup().id)}'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: storageSku }
  kind: 'StorageV2'
  properties: { supportsHttpsTrafficOnly: true }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  // PoC uses Azure Functions Consumption plan (Y1).
  sku: { name: 'Y1'; tier: 'Dynamic' }
  kind: 'functionapp'
}

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: { Application_Type: 'web' }
}

resource func 'Microsoft.Web/sites@2023-12-01' = {
  name: functionName
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: [
        { name: 'FUNCTIONS_WORKER_RUNTIME'; value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage'; value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storage.listKeys().keys[0].value}' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'; value: appi.properties.ConnectionString }
      ]
    }
    httpsOnly: true
  }
}

output functionName string = func.name
output storageAccountName string = storage.name
output principalId string = func.identity.principalId
