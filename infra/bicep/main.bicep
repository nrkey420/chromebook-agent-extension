// PoC infrastructure: Function App (Linux Consumption, .NET 8 isolated) + Storage + App Insights + Azure SQL.
// Azure SQL uses Microsoft Entra-only authentication (no SQL logins or passwords). The Function App connects
// with its system-assigned managed identity; its database user is created by infra/scripts/init-sql.*
// (run as the Entra admin), because Bicep cannot create database users.
// The HMAC key is stored in Key Vault and read through a Key Vault reference using the same identity.
// Exception: AzureWebJobsStorage stays a plain app setting because `az functionapp deployment source
// config-zip` on Linux Consumption parses it to upload the package. Moving to Flex Consumption allows an
// identity-based storage connection with no key at all.

@minLength(3)
@maxLength(20)
param prefix string = 'scpschrome'
param environment string = 'poc'
param location string = 'eastus'

@description('Object ID of the Microsoft Entra user or group that administers Azure SQL (a group is recommended).')
param sqlEntraAdminObjectId string

@description('Display name / UPN of the SQL Entra admin (shown in the portal; must match the principal).')
param sqlEntraAdminName string

@allowed([
  'User'
  'Group'
  'Application'
])
param sqlEntraAdminPrincipalType string = 'User'

@secure()
@description('Base64 HMAC shared secret for key id KEY1 (same value goes in the extension policy).')
param hmacKey string

@description('Optional object ID of a user or group to grant Key Vault Secrets Officer (read/rotate secrets). Leave empty to skip.')
param keyVaultAdminObjectId string = ''

var suffix = toLower(uniqueString(resourceGroup().id, prefix))
var storageName = take(toLower(replace('${prefix}${suffix}', '-', '')), 24)
var functionName = '${prefix}-func-${environment}'
var sqlServerName = '${prefix}-sql-${suffix}'
var sqlDbName = '${prefix}-db-${environment}'
var keyVaultName = '${take(prefix, 9)}kv${suffix}' // <= 24 chars

// Built-in role definition IDs.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  #disable-next-line BCP334 // prefix is at least 3 chars plus a 13-char unique suffix
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-law-${environment}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appi-${environment}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${prefix}-plan-${environment}'
  location: location
  kind: 'linux'
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {
    reserved: true // required for a Linux plan
  }
}

resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      login: sqlEntraAdminName
      sid: sqlEntraAdminObjectId
      tenantId: subscription().tenantId
      principalType: sqlEntraAdminPrincipalType
      azureADOnlyAuthentication: true
    }
  }
}

// Lets Azure services (including the Consumption-plan Function App, whose outbound IPs vary) reach SQL.
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sql
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: sqlDbName
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
}

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${sa.name};EndpointSuffix=${az.environment().suffixes.storage};AccountKey=${sa.listKeys().keys[0].value}'
// No secret: the Function App authenticates with its system-assigned managed identity.
var sqlConnectionString = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Initial Catalog=${db.name};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

resource hmacKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'HmacKey1'
  properties: { value: hmacKey }
}

// Versionless secret URIs, so a new secret version is picked up without redeploying.
func kvRef(secretUri string) string => '@Microsoft.KeyVault(SecretUri=${secretUri})'

resource func 'Microsoft.Web/sites@2023-12-01' = {
  name: functionName
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: storageConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appi.properties.ConnectionString }
        { name: 'SQL_CONNECTION_STRING', value: sqlConnectionString }
        { name: 'HMAC_KEYS__KEY1', value: kvRef(hmacKeySecret.properties.secretUri) }
        // WEBSITE_RUN_FROM_PACKAGE is set by `az functionapp deployment source config-zip` (Linux Consumption
        // does not support the value 1).
      ]
    }
  }
}

// The Function App's identity may read secrets (needed to resolve the Key Vault references).
resource funcSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(kv.id, func.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource adminSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(keyVaultAdminObjectId)) {
  scope: kv
  name: guid(kv.id, keyVaultAdminObjectId, keyVaultSecretsOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: keyVaultAdminObjectId
  }
}

output functionAppName string = func.name
output functionHostname string = func.properties.defaultHostName
output collectorUrl string = 'https://${func.properties.defaultHostName}'
output storageAccountName string = sa.name
output sqlServerName string = sql.name
output sqlServerFqdn string = sql.properties.fullyQualifiedDomainName
output sqlDatabaseName string = db.name
output keyVaultName string = kv.name
