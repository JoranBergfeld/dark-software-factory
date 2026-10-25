@description('Stable reserved factory prefix.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Reviewed factory hosting location.')
param location string

@description('Implemented runtime image including code-only readonly /authority, pinned by digest.')
@minLength(1)
@maxLength(512)
param runtimeImage string

@description('Reserved product key.')
@minLength(1)
@maxLength(90)
param productKey string

@description('Authoritative product configuration endpoint.')
param productEndpoint string

@description('Pinned inactive product profile.')
@minLength(64)
@maxLength(64)
param profilePin string

@description('Existing API registration tenant.')
param tenantId string

@description('Existing approved Entra API client ID.')
param audienceClientId string

@description('Only the reviewed operator may request capability proof.')
param operatorPrincipalId string

resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-hosting'
}

resource modelIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-council'
}

resource codeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-code'
}

resource modelHost 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-models'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${modelIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      // The existing factory environment must be internal; no network is changed here.
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: 'models'
        image: runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: ['serve-models']
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productEndpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: modelIdentity.properties.clientId }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource modelAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = {
  parent: modelHost
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
            allowedPrincipals: {
              identities: [operatorPrincipalId, codeIdentity.properties.principalId, modelIdentity.properties.principalId]
            }
          }
        }
      }
    }
  }
}
