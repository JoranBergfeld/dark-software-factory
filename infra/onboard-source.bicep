@description('Reserved factory-only prefix.')
@minLength(17)
@maxLength(17)
param namePrefix string

@description('Reviewed factory location.')
@minLength(1)
@maxLength(64)
param location string

@description('Immutable implemented runtime image digest.')
@minLength(1)
@maxLength(512)
param runtimeImage string

@description('Reserved product key.')
@minLength(1)
@maxLength(90)
param productKey string

@description('Authoritative product configuration store name.')
@minLength(37)
@maxLength(37)
param productConfigName string

@description('Isolated source/intent progress store; separate bounded namespaces, never product governance or model-access grants.')
@minLength(37)
@maxLength(37)
param progressConfigName string

@description('Pinned inactive product profile.')
@minLength(64)
@maxLength(64)
param profilePin string

@description('Reserved factory tenant.')
@minLength(36)
@maxLength(36)
param tenantId string

@description('Existing approved native Entra API registration with operator/code consent.')
@minLength(36)
@maxLength(36)
param audienceClientId string

@description('Only this operator may advance acquisition or private charter inference; code MI may materialize verified chunks.')
@minLength(36)
@maxLength(36)
param operatorPrincipalId string

@description('Exactly reviewed factory metadata Reader assignment ID.')
@minLength(36)
@maxLength(36)
param readerGrantName string

@description('Exactly reviewed product data-reader assignment ID.')
@minLength(36)
@maxLength(36)
param configGrantName string

@description('Exactly reviewed source/intent-store-only data-owner assignment ID; no governance writer.')
@minLength(36)
@maxLength(36)
param progressGrantName string

resource productConfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' existing = {
  name: productConfigName
}
resource codeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${namePrefix}-code'
}
resource hostingEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-hosting'
}
resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: '${namePrefix}-network'
}
resource privateSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: virtualNetwork
  name: 'private-endpoints'
}
resource privateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: 'privatelink.azconfig.io'
}
resource progressConfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' = {
  name: progressConfigName
  location: location
  sku: { name: 'Standard' }
  properties: {
    publicNetworkAccess: 'Disabled'
    disableLocalAuth: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
  }
}
resource privateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${namePrefix}-source-config'
  location: location
  properties: {
    subnet: { id: privateSubnet.id }
    privateLinkServiceConnections: [{
      name: 'source-progress'
      properties: { privateLinkServiceId: progressConfig.id, groupIds: ['configurationStores'] }
    }]
  }
}
resource privateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [{ name: 'source-progress', properties: { privateDnsZoneId: privateDnsZone.id } }]
  }
}
resource readerDefinition 'Microsoft.Authorization/roleDefinitions@2022-04-01' existing = {
  scope: subscription()
  name: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
}
resource configReaderDefinition 'Microsoft.Authorization/roleDefinitions@2022-04-01' existing = {
  scope: subscription()
  name: '516239f1-63e1-4d78-a4de-a74fb236a071'
}
resource dataOwnerDefinition 'Microsoft.Authorization/roleDefinitions@2022-04-01' existing = {
  scope: subscription()
  name: '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b'
}
resource metadataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: readerGrantName
  properties: { principalId: codeIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: readerDefinition.id }
}
resource productReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: productConfig
  name: configGrantName
  properties: { principalId: codeIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: configReaderDefinition.id }
}
resource progressWriter 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: progressConfig
  name: progressGrantName
  properties: { principalId: codeIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: dataOwnerDefinition.id }
}
resource sourceHost 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-source'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${codeIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: hostingEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      // Existing factory environment must be internal; no public network relaxation.
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      containers: [{
        name: 'source'
        image: runtimeImage
        command: ['dotnet', 'dsf-runtime.dll']
        args: ['serve-source']
        env: [
          { name: 'DSF_PRODUCT', value: productKey }
          { name: 'AZURE_APPCONFIG_ENDPOINT', value: productConfig.properties.endpoint }
          { name: 'DSF_FACTORY_PROFILE_PIN', value: profilePin }
          { name: 'AZURE_CLIENT_ID', value: codeIdentity.properties.clientId }
        ]
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}
resource sourceAuthentication 'Microsoft.App/containerApps/authConfigs@2025-01-01' = {
  parent: sourceHost
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
            allowedPrincipals: { identities: [operatorPrincipalId, codeIdentity.properties.principalId] }
          }
        }
      }
    }
  }
}
