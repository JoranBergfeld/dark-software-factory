// Per-product AKS cluster for the squad's Ralph watch loop (ADR 0012).
// KEDA add-on + OIDC issuer + workload identity, so pods scale 0..1 on the
// open squad:ready issue count and read the GitHub App token from Key Vault.

@description('Resource name prefix (matches the rest of the instance).')
param namePrefix string

@description('Azure region.')
param location string

@description('Product key (for tagging/traceability).')
param product string

resource aks 'Microsoft.ContainerService/managedClusters@2024-02-01' = {
  name: 'aks-dsf-${product}'
  location: location
  tags: {
    'dsf-product': product
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: '${namePrefix}-squad'
    enableRBAC: true
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    workloadAutoScalerProfile: {
      keda: {
        enabled: true
      }
    }
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: 1
        vmSize: 'Standard_D2s_v5'
        osType: 'Linux'
      }
    ]
  }
}

output aksName string = aks.name
output aksOidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
