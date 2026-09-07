@description('Globally unique owner Key Vault name.')
@minLength(3)
@maxLength(24)
param vaultName string

@description('Azure region for the owner Key Vault.')
param location string

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: vaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    accessPolicies: []
    // Bootstrap operators require data-plane access before a private network exists.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}
