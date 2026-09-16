@description('Stable reserved factory resource prefix.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Reviewed factory Azure location.')
param location string

@description('Implemented DSF runtime image pinned to its reviewed sha256 digest.')
param runtimeImage string

@description('Reserved product key.')
@minLength(1)
@maxLength(90)
param productKey string

@description('Authoritative product configuration endpoint.')
param productEndpoint string

@description('Immutable inactive profile pin.')
@minLength(64)
@maxLength(64)
param profilePin string

@description('Factory tenant; existing API registration must be single-tenant.')
param tenantId string

@description('Existing API registration for native Container Apps bearer authentication; no registration or consent is created.')
param audienceClientId string

@description('Approved operator object ID; may verify but cannot receive installation tokens.')
param operatorPrincipalId string

resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-hosting'
}

resource githubIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-github'
}

resource codeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-code'
}

resource githubAuthentication 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-github-auth'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${githubIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
    }
    template: {
      containers: [{
        name: 'github-auth'
        image: runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: ['serve-github-auth']
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productEndpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: githubIdentity.properties.clientId }
          { name: 'DSF_GITHUB_AUTH_AUDIENCE', value: audienceClientId }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource githubAuthenticationPolicy 'Microsoft.App/containerApps/authConfigs@2025-01-01' = {
  parent: githubAuthentication
  name: 'current'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      unauthenticatedClientAction: 'Return401'
      excludedPaths: []
    }
    httpSettings: { requireHttps: true }
    login: { tokenStore: { enabled: false } }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: audienceClientId
          openIdIssuer: uri(environment().authentication.loginEndpoint, '${tenantId}/v2.0')
        }
        validation: {
          allowedAudiences: [audienceClientId, 'api://${audienceClientId}']
          defaultAuthorizationPolicy: {
            allowedPrincipals: {
              identities: [operatorPrincipalId, codeIdentity.properties.principalId]
            }
          }
        }
      }
    }
  }
}
