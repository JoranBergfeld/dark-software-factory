@description('Stable prefix of the original reserved factory.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Original reviewed factory location.')
param location string

@description('Original product key.')
@minLength(1)
@maxLength(90)
param productKey string

@description('Original protected product configuration endpoint.')
param productEndpoint string

@description('Original restrictive onboarding profile pin.')
@minLength(64)
@maxLength(64)
param profilePin string

@description('Original factory tenant.')
@minLength(36)
@maxLength(36)
param tenantId string

@description('Original effective governance operator object ID.')
@minLength(36)
@maxLength(36)
param operatorPrincipalId string

@description('Separately reviewed immutable live runtime image digest.')
@minLength(1)
@maxLength(512)
param runtimeImage string

@description('Existing reviewed live-host Entra API registration, never created implicitly.')
@minLength(36)
@maxLength(36)
param audienceClientId string

resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-hosting'
}

resource councilIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-council'
}

// No new identity, state/backend permission or original-host replacement.
resource decideHost 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-decide'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${councilIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: 'decide'
        image: runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: ['serve-decide']
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productEndpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: councilIdentity.properties.clientId }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource decideAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = {
  parent: decideHost
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
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [audienceClientId, 'api://${audienceClientId}']
          defaultAuthorizationPolicy: {
            allowedPrincipals: { identities: [operatorPrincipalId, councilIdentity.properties.principalId] }
          }
        }
      }
    }
  }
}

output councilEndpoint string = 'https://${decideHost.properties.configuration.ingress.fqdn}'
