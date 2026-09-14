@description('Name of the existing product Key Vault.')
@minLength(3)
@maxLength(24)
param vaultName string

@secure()
@description('GitHub App private key PEM to store in the product Key Vault.')
param githubAppPrivateKey string

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vaultName
}

resource githubAppPrivateKeySecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'github-app-private-key'
  properties: {
    value: githubAppPrivateKey
  }
}
