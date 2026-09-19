@description('Original reserved factory prefix.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Original factory location.')
param location string

@description('Original product and database name.')
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

@description('Original governance operator, permitted only at the new council context host.')
@minLength(36)
@maxLength(36)
param operatorPrincipalId string

@description('Separately reviewed immutable publisher and context image digest.')
@minLength(1)
@maxLength(512)
param runtimeImage string

@description('Existing reviewed Entra API registration; never created implicitly.')
@minLength(36)
@maxLength(36)
param audienceClientId string

@description('Exact reviewed native AUTH publication role ID.')
@minLength(36)
@maxLength(36)
param writerRoleId string

@description('Exact reviewed native COUNCIL read-only publication role ID.')
@minLength(36)
@maxLength(36)
param readerRoleId string

@description('Exact reviewed native AUTH publication-container assignment ID.')
@minLength(36)
@maxLength(36)
param writerAssignmentId string

@description('Exact reviewed native COUNCIL publication-container assignment ID.')
@minLength(36)
@maxLength(36)
param readerAssignmentId string

resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-hosting'
}
resource authIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-github'
}
resource councilIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-council'
}
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: '${namePrefix}-cosmos'
}
resource stateDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' existing = {
  parent: cosmosAccount
  name: productKey
}
resource publicationsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: stateDatabase
  name: 'proposal-publications'
  properties: {
    resource: {
      id: 'proposal-publications'
      partitionKey: { paths: ['/product'], kind: 'Hash', version: 2 }
    }
  }
}
resource writerRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-11-15' = {
  parent: cosmosAccount
  name: writerRoleId
  properties: {
    roleName: 'DSF AUTH create-once publication journal'
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
resource readerRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-11-15' = {
  parent: cosmosAccount
  name: readerRoleId
  properties: {
    roleName: 'DSF COUNCIL publication receipts read-only'
    type: 'CustomRole'
    assignableScopes: [cosmosAccount.id]
    permissions: [{
      dataActions: [
        'Microsoft.DocumentDB/databaseAccounts/readMetadata'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read'
      ]
    }]
  }
}
resource writerAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: writerAssignmentId
  properties: {
    roleDefinitionId: writerRole.id
    principalId: authIdentity.properties.principalId
    scope: '${cosmosAccount.id}/dbs/${productKey}/colls/${publicationsContainer.properties.resource.id}'
  }
}
resource readerAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: readerAssignmentId
  properties: {
    roleDefinitionId: readerRole.id
    principalId: councilIdentity.properties.principalId
    scope: '${cosmosAccount.id}/dbs/${productKey}/colls/${publicationsContainer.properties.resource.id}'
  }
}

var hostSuffixes = ['publisher', 'pub-context']

// New isolated hosts reuse original identities; no key copy, identity creation or original-host replacement.
resource publicationHosts 'Microsoft.App/containerApps@2025-01-01' = [for (suffix, index) in hostSuffixes: {
  name: '${namePrefix}-${suffix}'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${index == 0 ? authIdentity.id : councilIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: suffix
        image: runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: [index == 0 ? 'serve-publisher' : 'serve-publication-context']
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productEndpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: index == 0 ? authIdentity.properties.clientId : councilIdentity.properties.clientId }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}]
resource publicationAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = [for (suffix, index) in hostSuffixes: {
  parent: publicationHosts[index]
  name: 'current'
  properties: {
    platform: { enabled: true }
    globalValidation: { unauthenticatedClientAction: 'Return401', excludedPaths: [] }
    httpSettings: { requireHttps: true }
    login: { tokenStore: { enabled: false } }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: { clientId: audienceClientId, openIdIssuer: '${environment().authentication.loginEndpoint}${tenantId}/v2.0' }
        validation: {
          allowedAudiences: [audienceClientId, 'api://${audienceClientId}']
          defaultAuthorizationPolicy: {
            allowedPrincipals: {
              identities: index == 0 ? [councilIdentity.properties.principalId] : [authIdentity.properties.principalId, operatorPrincipalId]
            }
            allowedApplications: index == 0 ? [councilIdentity.properties.clientId] : []
          }
        }
      }
    }
  }
}]

output councilEndpoint string = 'https://${publicationHosts[1].properties.configuration.ingress.fqdn}'
