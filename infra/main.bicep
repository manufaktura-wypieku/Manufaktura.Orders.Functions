@description('Environment name used as suffix for all resources.')
@allowed(['dev', 'test', 'prod'])
param environmentName string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Maximum number of Function App instances. Set higher for production to allow scale-out.')
@minValue(1)
@maxValue(200)
param maximumInstanceCount int = 10

@description('Dataverse environment URL, e.g. https://org.crm.dynamics.com')
param dataverseUrl string

@description('SharePoint site URL, e.g. https://tenant.sharepoint.com/sites/name')
param sharePointSiteUrl string

// Naming convention
var suffix = 'mfk-orders-${environmentName}'
var storageNamePrefix = toLower(replace('stmfkord${environmentName}', '-', ''))
var storageName = substring('${storageNamePrefix}${uniqueString(resourceGroup().id)}', 0, 24)
var functionAppName = 'func-${suffix}'
var appServicePlanName = 'asp-${suffix}'
var appInsightsName = 'ai-${suffix}'
var logAnalyticsName = 'log-${suffix}'

// Common tags for cost filtering
var tags = {
  Environment: environmentName
  Project: 'Manufaktura.Orders'
}

// Log Analytics workspace for Application Insights
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// Storage account (required by Azure Functions)
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // Enforce identity-based access; no storage keys on the Function App
  }
}

// Flex Consumption plan (per-second billing, faster cold starts)
resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true // Linux required for Flex Consumption
  }
}

// Storage container for Flex Consumption deployment packages
resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storageAccount.name}/default/deploymentpackage'
  properties: {
    publicAccess: 'None'
  }
}

// Function App with system-assigned Managed Identity
resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      appSettings: [
        {
          // Identity-based storage connection — no account key stored in app settings
          name: 'AzureWebJobsStorage__accountName'
          value: storageAccount.name
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'DataverseUrl'
          value: dataverseUrl
        }
        {
          name: 'SharePointSiteUrl'
          value: sharePointSiteUrl
        }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}deploymentpackage'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: 512
      }
    }
  }
  dependsOn: [
    deploymentContainer
  ]
}

// Built-in role IDs for identity-based Azure Functions storage access
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

// Grant the Function App managed identity access to the storage account
resource storageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataContributorRoleId
    )
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageQueueDataContributorRoleId
    )
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageTableContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageTableDataContributorRoleId
    )
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Function App name for deployment.')
output functionAppName string = functionApp.name

@description('Function App default hostname.')
output functionAppHostname string = functionApp.properties.defaultHostName

@description('Managed Identity principal ID — run setup-github-oidc.ps1 to grant Sites.ReadWrite.All on Microsoft Graph for SharePoint access.')
output managedIdentityPrincipalId string = functionApp.identity.principalId
