param prefix string = 'scpschrome'
param environment string = 'poc'
param location string = 'eastus'
@secure() param sqlAdminPassword string = 'ChangeMe123!'
param sqlAdminLogin string = 'sqladminpoc'
var suffix = toLower(uniqueString(resourceGroup().id, prefix))
var storageName = substring(replace('${prefix}${suffix}','-',''),0,24)
var functionName = '${prefix}-func-${environment}'
var appInsightsName = '${prefix}-appi-${environment}'
var sqlServerName = '${prefix}-sql-${suffix}'
var sqlDbName = '${prefix}-db-${environment}'

resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: { minimumTlsVersion: 'TLS1_2', allowBlobPublicAccess: false }
}
resource appi 'microsoft.insights/components@2020-02-02' = { name: appInsightsName location: location kind: 'web' properties: { Application_Type:'web' } }
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = { name: '${prefix}-plan-${environment}' location: location sku: { name:'Y1', tier:'Dynamic' } kind:'functionapp' }
resource func 'Microsoft.Web/sites@2023-12-01' = {
  name: functionName
  location: location
  kind: 'functionapp,linux'
  identity: { type:'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        { name:'AzureWebJobsStorage', value:'DefaultEndpointsProtocol=https;AccountName=${sa.name};EndpointSuffix=core.windows.net' }
        { name:'FUNCTIONS_WORKER_RUNTIME', value:'dotnet-isolated' }
        { name:'FUNCTIONS_EXTENSION_VERSION', value:'~4' }
        { name:'WEBSITE_RUN_FROM_PACKAGE', value:'1' }
      ]
    }
  }
}
resource sql 'Microsoft.Sql/servers@2022-05-01-preview' = { name:sqlServerName location:location properties:{administratorLogin:sqlAdminLogin,administratorLoginPassword:sqlAdminPassword,version:'12.0'} }
resource db 'Microsoft.Sql/servers/databases@2022-05-01-preview' = { parent:sql name:sqlDbName location:location sku:{name:'Basic'} }
output functionHostname string = func.properties.defaultHostName
output storageAccountName string = sa.name
output sqlServerName string = sql.name
output sqlDatabaseName string = db.name
