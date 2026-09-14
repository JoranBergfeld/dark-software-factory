@description('Prefix for this isolated instance network.')
@minLength(3)
@maxLength(12)
param namePrefix string

@description('Azure region for the instance network.')
param location string = resourceGroup().location

@description('Existing product Key Vault in this resource group.')
@minLength(3)
@maxLength(24)
param keyVaultName string

@description('Existing product Cosmos DB account in this resource group.')
@minLength(3)
@maxLength(44)
param cosmosAccountName string

@description('Isolated VNet address space; choose a nonoverlapping range if adding peering.')
param addressPrefix string = '10.173.0.0/16'

@description('Dedicated Container Apps infrastructure subnet.')
param infrastructureAddressPrefix string = '10.173.0.0/23'

@description('Private endpoint subnet.')
param privateEndpointAddressPrefix string = '10.173.2.0/24'

resource keyVault 'Microsoft.KeyVault/vaults@2026-05-15' existing = {
  name: keyVaultName
}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2026-03-15' existing = {
  name: cosmosAccountName
}

resource networkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2026-03-01' = {
  name: '${namePrefix}-network-security'
  location: location
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2026-03-01' = {
  name: '${namePrefix}-network'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [addressPrefix]
    }
    subnets: [
      {
        name: 'apps'
        properties: {
          addressPrefix: infrastructureAddressPrefix
          networkSecurityGroup: {
            id: networkSecurityGroup.id
          }
          delegations: [
            {
              name: 'container-apps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointAddressPrefix
          privateEndpointNetworkPolicies: 'Disabled'
          networkSecurityGroup: {
            id: networkSecurityGroup.id
          }
        }
      }
    ]
  }
}

resource vaultPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
}

resource cosmosPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.documents.azure.com'
  location: 'global'
}

resource vaultDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: vaultPrivateDnsZone
  name: namePrefix
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}

resource cosmosDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: cosmosPrivateDnsZone
  name: namePrefix
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}

resource vaultPrivateEndpoint 'Microsoft.Network/privateEndpoints@2026-03-01' = {
  name: '${namePrefix}-vault-private'
  location: location
  properties: {
    subnet: {
      id: '${virtualNetwork.id}/subnets/private-endpoints'
    }
    privateLinkServiceConnections: [
      {
        name: 'vault'
        properties: {
          privateLinkServiceId: keyVault.id
          groupIds: ['vault']
        }
      }
    ]
  }
}

resource cosmosPrivateEndpoint 'Microsoft.Network/privateEndpoints@2026-03-01' = {
  name: '${namePrefix}-cosmos-private'
  location: location
  properties: {
    subnet: {
      id: '${virtualNetwork.id}/subnets/private-endpoints'
    }
    privateLinkServiceConnections: [
      {
        name: 'cosmos'
        properties: {
          privateLinkServiceId: cosmosAccount.id
          groupIds: ['Sql']
        }
      }
    ]
  }
}

resource vaultDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2026-03-01' = {
  parent: vaultPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'vault'
        properties: {
          privateDnsZoneId: vaultPrivateDnsZone.id
        }
      }
    ]
  }
}

resource cosmosDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2026-03-01' = {
  parent: cosmosPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'cosmos'
        properties: {
          privateDnsZoneId: cosmosPrivateDnsZone.id
        }
      }
    ]
  }
}

output infrastructureSubnetId string = '${virtualNetwork.id}/subnets/apps'
output virtualNetworkId string = virtualNetwork.id
