// containerapp.bicep
// Reusable module: provisions one Azure Container App on a shared managed
// environment, attached to a user-assigned managed identity. The image is
// expected to be supplied by azd at deploy time; a placeholder is used so the
// template provisions cleanly before any image exists.

@description('Container App name.')
param name string

@description('Azure region.')
param location string

@description('Resource ID of the Container Apps managed environment.')
param environmentId string

@description('Resource ID of the user-assigned managed identity to attach.')
param identityId string

@description('Container image reference (registry/repo:tag). Placeholder until azd builds & pushes.')
param image string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Whether ingress is enabled for this app.')
param ingressEnabled bool = true

@description('Whether ingress is exposed externally (true) or only inside the environment (false).')
param externalIngress bool = false

@description('Target container port for ingress.')
param targetPort int = 8080

@description('Environment variables (name/value or name/secretRef) passed to the container.')
param env array = []

@description('Minimum replica count.')
param minReplicas int = 0

@description('Maximum replica count.')
param maxReplicas int = 2

@description('Tags applied to the resource.')
param tags object = {}

@description('azd service name, surfaced as the azd-service-name tag so `azd deploy` can target this app.')
param serviceName string = name

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: union(tags, {
    'azd-service-name': serviceName
  })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: ingressEnabled ? {
        external: externalIngress
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      } : null
    }
    template: {
      containers: [
        {
          name: name
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1.0Gi'
          }
          env: env
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

@description('The container app resource ID.')
output id string = app.id

@description('The container app name.')
output name string = app.name

@description('Fully-qualified ingress domain, or empty string if ingress is disabled.')
output fqdn string = ingressEnabled ? app.properties.configuration.ingress.fqdn : ''
