@description('Stable prefix derived from the reserved factory ID; never an application resource prefix.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Original reviewed factory location.')
param location string

@description('Reserved product key and original Cosmos database name.')
@minLength(1)
@maxLength(90)
param productKey string

@description('Original protected product configuration endpoint.')
param productEndpoint string

@description('Original inactive factory profile pin.')
@minLength(64)
@maxLength(64)
param profilePin string

@description('Original factory tenant.')
@minLength(36)
@maxLength(36)
param tenantId string

@description('Effective operator object ID from the admitted factory identity.')
@minLength(36)
@maxLength(36)
param operatorPrincipalId string

@description('Digest-pinned new council and source-context image; original hosts are never upgraded.')
@minLength(1)
@maxLength(512)
param runtimeImage string

@description('Reviewed existing Entra API registration for council and context consumption.')
@minLength(36)
@maxLength(36)
param audienceClientId string

@description('Digest-pinned new model-consumption image.')
@minLength(1)
@maxLength(512)
param modelRuntimeImage string

@description('Reviewed existing Entra model-consumption API registration.')
@minLength(36)
@maxLength(36)
param modelAudienceClientId string

@description('Digest-pinned new telemetry-consumption image.')
@minLength(1)
@maxLength(512)
param telemetryRuntimeImage string

@description('Reviewed existing Entra telemetry-consumption API registration.')
@minLength(36)
@maxLength(36)
param telemetryAudienceClientId string

@description('Only currently selected original supplemental sources; empty means no supplemental resource or setting.')
@maxLength(2)
param supplementalSources supplementalSource[] = []

@description('Exact reviewed SQL role identifier from FactoryPreviewValidation.SqlRole; not Bicep guid().')
@minLength(36)
@maxLength(36)
param stateRoleId string

@description('Exact reviewed runs assignment identifier from FactoryPreviewValidation.SqlAssignment.')
@minLength(36)
@maxLength(36)
param runsAssignmentId string

@description('Exact reviewed council-preview assignment identifier from FactoryPreviewValidation.SqlAssignment.')
@minLength(36)
@maxLength(36)
param previewAssignmentId string

@sealed()
type supplementalSource = {
  kind: 'foundryiq' | 'webiq'
  @minLength(1)
  @maxLength(512)
  runtimeImage: string
  @minLength(36)
  @maxLength(36)
  audienceClientId: string
  @minLength(1)
  @maxLength(16384)
  workloadConfiguration: string
}

resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-hosting'
}

resource councilIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-council'
}

resource codeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-code'
}

resource telemetryIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-source'
}

resource supplementalIdentities 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = [for source in supplementalSources: {
  name: '${namePrefix}-${source.kind}'
}]

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: '${namePrefix}-cosmos'
}

resource stateDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' existing = {
  parent: cosmosAccount
  name: productKey
}

resource previewContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: stateDatabase
  name: 'council-preview'
  properties: {
    resource: {
      id: 'council-preview'
      partitionKey: { paths: ['/product'], kind: 'Hash', version: 2 }
    }
  }
}

resource stateRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-11-15' = {
  parent: cosmosAccount
  name: stateRoleId
  properties: {
    roleName: 'DSF supervised preview state'
    type: 'CustomRole'
    assignableScopes: [cosmosAccount.id]
    permissions: [{
      dataActions: [
        'Microsoft.DocumentDB/databaseAccounts/readMetadata'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/replace'
      ]
    }]
  }
}

resource runsAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: runsAssignmentId
  properties: {
    roleDefinitionId: stateRole.id
    principalId: councilIdentity.properties.principalId
    scope: '${cosmosAccount.id}/dbs/${productKey}/colls/runs'
  }
}

resource previewAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: previewAssignmentId
  properties: {
    roleDefinitionId: stateRole.id
    principalId: councilIdentity.properties.principalId
    scope: '${cosmosAccount.id}/dbs/${productKey}/colls/${previewContainer.properties.resource.id}'
  }
}

resource councilHost 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-preview'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${councilIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      // The existing managed environment must already have internal ingress.
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: 'preview'
        image: runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: ['serve-preview']
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

// Variable comprehensions cannot dereference existing resource collections; selection is bounded to two.
var supplementalCallerPrincipals = empty(supplementalSources) ? [] : length(supplementalSources) == 1
  ? [supplementalIdentities[0].properties.principalId]
  : [supplementalIdentities[0].properties.principalId, supplementalIdentities[1].properties.principalId]

resource councilAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = {
  parent: councilHost
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
              identities: concat([operatorPrincipalId], supplementalCallerPrincipals)
            }
          }
        }
      }
    }
  }
}

var consumerSpecifications = [
  { suffix: 'context', verb: 'serve-source-context', identity: codeIdentity.id, image: runtimeImage, audience: audienceClientId }
  { suffix: 'preview-models', verb: 'serve-preview-models', identity: councilIdentity.id, image: modelRuntimeImage, audience: modelAudienceClientId }
  { suffix: 'preview-telemetry', verb: 'serve-preview-telemetry', identity: telemetryIdentity.id, image: telemetryRuntimeImage, audience: telemetryAudienceClientId }
]

var consumerClientIds = [codeIdentity.properties.clientId, councilIdentity.properties.clientId, telemetryIdentity.properties.clientId]

resource consumerHosts 'Microsoft.App/containerApps@2025-01-01' = [for (consumer, i) in consumerSpecifications: {
  name: '${namePrefix}-${consumer.suffix}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${consumer.identity}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: consumer.suffix
        image: consumer.image
        command: ['dotnet', 'dsf-runtime.dll']
        args: [consumer.verb]
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productEndpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: consumerClientIds[i] }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}]

resource consumerAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = [for (consumer, i) in consumerSpecifications: {
  parent: consumerHosts[i]
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
          clientId: consumer.audience
          openIdIssuer: uri(environment().authentication.loginEndpoint, '${tenantId}/v2.0')
        }
        validation: {
          allowedAudiences: [consumer.audience, 'api://${consumer.audience}']
          defaultAuthorizationPolicy: {
            allowedApplications: [councilIdentity.properties.clientId]
            allowedPrincipals: { identities: [councilIdentity.properties.principalId] }
          }
        }
      }
    }
  }
}]

resource supplementalHosts 'Microsoft.App/containerApps@2025-01-01' = [for (source, i) in supplementalSources: {
  name: '${namePrefix}-preview-${source.kind}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${supplementalIdentities[i].id}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: 'preview-${source.kind}'
        image: source.runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: ['serve-preview-supplemental']
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productEndpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: supplementalIdentities[i].properties.clientId }
          { name: 'DSF_SUPPLEMENTAL_CONFIG', value: source.workloadConfiguration }
          // Preserve PreviewSupplementalCoordinator serialization, including property order.
          { name: 'DSF_PREVIEW_COORDINATOR', value: '{"endpoint":"https://${councilHost.properties.configuration.ingress.fqdn}","audienceClientId":"${audienceClientId}","principalId":"${councilIdentity.properties.principalId}","clientId":"${councilIdentity.properties.clientId}"}' }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}]

resource supplementalAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = [for (source, i) in supplementalSources: {
  parent: supplementalHosts[i]
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
          clientId: source.audienceClientId
          openIdIssuer: uri(environment().authentication.loginEndpoint, '${tenantId}/v2.0')
        }
        validation: {
          allowedAudiences: [source.audienceClientId, 'api://${source.audienceClientId}']
          defaultAuthorizationPolicy: {
            allowedApplications: [councilIdentity.properties.clientId]
            allowedPrincipals: { identities: [councilIdentity.properties.principalId] }
          }
        }
      }
    }
  }
}]

@description('Actual native COUNCIL host endpoint, never a guessed ACA hostname.')
output councilEndpoint string = 'https://${councilHost.properties.configuration.ingress.fqdn}'
