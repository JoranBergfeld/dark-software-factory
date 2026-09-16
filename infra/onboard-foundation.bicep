@description('Stable factory-only resource prefix derived from the reserved Factory ID.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Exactly the reviewed factory location.')
param location string

@description('Already-created product App Configuration; never the owner store.')
@minLength(37)
@maxLength(37)
param appConfigName string

@description('Reserved Azure tenant for the factory vault.')
param tenantId string

@description('Reserved product key, also the isolated runtime database name.')
@minLength(1)
@maxLength(90)
param productKey string

resource appConfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' existing = {
  name: appConfigName
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: '${namePrefix}-vault'
  location: location
  properties: {
    tenantId: tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Disabled'
  }
}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: '${namePrefix}-cosmos'
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [{ locationName: location, failoverPriority: 0, isZoneRedundant: false }]
    capabilities: [{ name: 'EnableNoSQLVectorSearch' }]
    enableFreeTier: false
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
  }
}

resource runtimeDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmosAccount
  name: productKey
  properties: {
    resource: { id: productKey }
    options: { autoscaleSettings: { maxThroughput: 1000 } }
  }
}

var idPartitionedStores = ['working', 'records', 'lessons', 'charters']
resource memoryContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for store in idPartitionedStores: {
  parent: runtimeDatabase
  name: store
  properties: {
    resource: {
      id: store
      partitionKey: { paths: ['/id'], kind: 'Hash' }
      defaultTtl: -1
    }
  }
}]

var productPartitionedStores = ['runs', 'learning']
resource runtimeContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for store in productPartitionedStores: {
  parent: runtimeDatabase
  name: store
  properties: {
    resource: {
      id: store
      partitionKey: { paths: ['/product'], kind: 'Hash' }
      defaultTtl: -1
    }
  }
}]

resource factoryLogs 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: '${namePrefix}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    publicNetworkAccessForIngestion: 'Disabled'
    publicNetworkAccessForQuery: 'Disabled'
  }
}

resource factoryInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: factoryLogs.id
    publicNetworkAccessForIngestion: 'Disabled'
    publicNetworkAccessForQuery: 'Disabled'
    DisableLocalAuth: true
  }
}

var identityKinds = ['council', 'source', 'code', 'github']
resource workloadIdentities 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = [for kind in identityKinds: {
  name: '${namePrefix}-${kind}'
  location: location
}]

resource networkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: '${namePrefix}-nsg'
  location: location
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${namePrefix}-network'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.173.0.0/16'] }
    subnets: [
      {
        name: 'apps'
        properties: {
          addressPrefix: '10.173.0.0/23'
          networkSecurityGroup: { id: networkSecurityGroup.id }
          delegations: [{ name: 'container-apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: '10.173.2.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
          networkSecurityGroup: { id: networkSecurityGroup.id }
        }
      }
    ]
  }
}

var privateServices = [
  { suffix: 'vault', zone: 'privatelink.vaultcore.azure.net', target: keyVault.id, group: 'vault' }
  { suffix: 'cosmos', zone: 'privatelink.documents.azure.com', target: cosmosAccount.id, group: 'Sql' }
  { suffix: 'config', zone: 'privatelink.azconfig.io', target: appConfig.id, group: 'configurationStores' }
]

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for service in privateServices: {
  name: service.zone
  location: 'global'
}]

resource privateDnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (service, index) in privateServices: {
  parent: privateDnsZones[index]
  name: namePrefix
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: virtualNetwork.id }
  }
}]

resource privateEndpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [for service in privateServices: {
  name: '${namePrefix}-${service.suffix}-private'
  location: location
  properties: {
    subnet: { id: '${virtualNetwork.id}/subnets/private-endpoints' }
    privateLinkServiceConnections: [{
      name: service.suffix
      properties: { privateLinkServiceId: service.target, groupIds: [service.group] }
    }]
  }
}]

resource privateDnsZoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [for (service, index) in privateServices: {
  parent: privateEndpoints[index]
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [{ name: service.suffix, properties: { privateDnsZoneId: privateDnsZones[index].id } }]
  }
}]

resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${namePrefix}-hosting'
  location: location
  properties: {
    infrastructureResourceGroup: '${namePrefix}-hosting-infra'
    vnetConfiguration: { infrastructureSubnetId: '${virtualNetwork.id}/subnets/apps', internal: true }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    appLogsConfiguration: { destination: 'none' }
  }
}
