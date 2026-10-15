@description('Stable reserved factory prefix.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Reviewed factory location.')
param location string

@description('Implemented runtime image pinned to a reviewed sha256 digest.')
param runtimeImage string

@description('Reserved product key.')
@minLength(1)
@maxLength(90)
param productKey string

@description('Authoritative product configuration endpoint.')
param productEndpoint string

@description('Pinned inactive profile.')
@minLength(64)
@maxLength(64)
param profilePin string

@description('Existing single-tenant API registration tenant.')
param tenantId string

@description('Existing approved API client ID; no registration or consent is created.')
param audienceClientId string

@description('Only this operator may request readiness proof; no publishing caller is admitted.')
param operatorPrincipalId string

resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-hosting'
}

resource sourceIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-source'
}

resource telemetrySource 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-telemetry'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${sourceIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      // Ingress is reachable only inside the proven internal factory environment.
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: 'telemetry'
        image: runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: ['serve-telemetry']
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productEndpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: sourceIdentity.properties.clientId }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource telemetryAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = {
  parent: telemetrySource
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
