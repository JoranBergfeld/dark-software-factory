targetScope = 'subscription'

@description('Resource group containing the owner Key Vault.')
param ownerResourceGroup string

@description('Name of the existing owner Key Vault.')
@minLength(3)
@maxLength(24)
param ownerVaultName string

@description('Resource group containing the product Key Vault.')
param productResourceGroup string

@description('Name of the existing product Key Vault.')
@minLength(3)
@maxLength(24)
param productVaultName string

resource ownerGroup 'Microsoft.Resources/resourceGroups@2024-11-01' existing = {
  name: ownerResourceGroup
}

resource productGroup 'Microsoft.Resources/resourceGroups@2024-11-01' existing = {
  name: productResourceGroup
}

resource ownerKeyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: ownerVaultName
  scope: ownerGroup
}

module productSecret 'product-github-private-key.bicep' = {
  name: 'dsf-product-github-private-key-${uniqueString(ownerKeyVault.id, productVaultName)}'
  scope: productGroup
  params: {
    vaultName: productVaultName
    githubAppPrivateKey: ownerKeyVault.getSecret('github-app-private-key')
  }
}
