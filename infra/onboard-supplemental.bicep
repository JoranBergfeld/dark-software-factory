@description('Stable reserved factory prefix.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Explicitly selected source; no hosts for unselected sources.')
@allowed(['foundryiq', 'webiq'])
param sourceKind string

@description('Approved new factory location.')
param location string

@description('Implemented runtime image pinned by sha256.')
param runtimeImage string

@description('Reserved product key.')
@minLength(1)
@maxLength(90)
param productKey string

@description('Product authority locator only. Source identity receives no store access.')
param productEndpoint string

@description('Pinned inactive profile.')
@minLength(64)
@maxLength(64)
param profilePin string

@description('Existing single-tenant API registration tenant.')
param tenantId string

@description('Existing reviewed API client ID; no registration or consent creation.')
param audienceClientId string

@description('Only this native operator may request proof; source cannot approve governance.')
param operatorPrincipalId string

@description('Reviewed nonsecret immutable source configuration. Never a secret value, private repository or application input.')
@maxLength(8192)
param sourceConfiguration string

resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-hosting'
}
resource sourceIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-${sourceKind}'
}
resource supplementalSource 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-${sourceKind}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${sourceIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: sourceKind
        image: runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: ['serve-supplemental']
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productEndpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: sourceIdentity.properties.clientId }
          { name: 'DSF_SUPPLEMENTAL_CONFIG', value: sourceConfiguration }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}
resource supplementalAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = {
  parent: supplementalSource
  name: 'current'
  properties: {
    platform: { enabled: true }
    globalValidation: { unauthenticatedClientAction: 'Return401', excludedPaths: [] }
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
            allowedPrincipals: { identities: [operatorPrincipalId] }
          }
        }
      }
    }
  }
}
