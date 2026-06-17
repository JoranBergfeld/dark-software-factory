// ingestion.bicep
// Signal-ingestion buffer: an Event Grid custom topic where external signal
// sources (Sentry/GitHub/etc. via a relay) publish, delivered into a Service Bus
// queue that the homelab orchestrator polls OUTBOUND. Nothing is ever pushed into
// the homelab — this is the egress-only ingestion path (design §8, ADR 0002).
//
// Delivery uses the topic's system-assigned identity (no SAS keys): the topic is
// granted Azure Service Bus Data Sender on the queue, and the event subscription
// delivers with that resource identity. The homelab workload is granted Data
// Receiver so it can dequeue.

@description('Short name prefix.')
param namePrefix string

@description('Unique suffix shared with the parent template.')
param suffix string

@description('Azure region.')
param location string

@description('Tags applied to resources.')
param tags object = {}

@description('Object ID of the homelab workload SP granted Service Bus Data Receiver. Empty = skip.')
param receiverPrincipalId string = ''

@description('Gate public network access to the Event Grid topic. Defaults to false. Enable only when private endpoint connectivity is unavailable.')
param allowPublicNetworkAccess bool = false

@description('Name of the signals queue.')
param queueName string = 'signals'

// Built-in Service Bus data-plane role IDs.
var sbDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' // Azure Service Bus Data Sender
var sbDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0' // Azure Service Bus Data Receiver

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: '${namePrefix}-sb-${suffix}'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {}
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: queueName
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
  }
}

resource topic 'Microsoft.EventGrid/topics@2024-06-01-preview' = {
  name: '${namePrefix}-egt-${suffix}'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    inputSchema: 'EventGridSchema'
    publicNetworkAccess: allowPublicNetworkAccess ? 'Enabled' : 'Disabled'
  }
}

// The topic's identity may send to the namespace (so it can deliver to the queue).
resource topicSenderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, topic.id, sbDataSenderRoleId)
  scope: serviceBus
  properties: {
    principalId: topic.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sbDataSenderRoleId)
  }
}

// NOTE: the Event Grid event subscription that delivers topic events into the
// Service Bus queue (identity-based) is created POST-DEPLOY via the Azure CLI, not
// here. Event Grid validates managed-identity delivery synchronously at creation,
// which races RBAC propagation of `topicSenderAssignment` above and fails an
// in-template subscription (aka.ms/egmsivalidation). Create it once the role has
// propagated (see infra/README.md):
//   az eventgrid event-subscription create \
//     --name to-signals-queue --source-resource-id <topicId> \
//     --delivery-identity systemassigned \
//     --endpoint-type servicebusqueue --endpoint <queueId>

// The homelab workload may receive (dequeue) from the namespace.
resource receiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(receiverPrincipalId)) {
  name: guid(serviceBus.id, receiverPrincipalId, sbDataReceiverRoleId)
  scope: serviceBus
  properties: {
    principalId: receiverPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sbDataReceiverRoleId)
  }
}

@description('Event Grid custom topic endpoint (publish signals here).')
output topicEndpoint string = topic.properties.endpoint

@description('Service Bus namespace hostname for outbound polling.')
output namespaceHostname string = '${serviceBus.name}.servicebus.windows.net'

@description('Signals queue name.')
output queueName string = queue.name
