@description('Name of the existing owner Key Vault.')
@minLength(3)
@maxLength(24)
param vaultName string

@secure()
@description('GitHub App identifier to store in the owner Key Vault.')
param githubAppId string

@secure()
@description('GitHub App installation identifier to store in the owner Key Vault.')
param githubInstallationId string

@secure()
@description('GitHub App private key PEM to store in the owner Key Vault.')
param githubAppPrivateKey string

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vaultName
}

resource githubAppIdSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'github-app-id'
  properties: {
    value: githubAppId
  }
}

resource githubInstallationIdSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'github-app-installation-id'
  properties: {
    value: githubInstallationId
  }
}

resource githubAppPrivateKeySecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'github-app-private-key'
  properties: {
    value: githubAppPrivateKey
  }
}
