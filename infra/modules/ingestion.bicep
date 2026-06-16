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
    publicNetworkAccess: 'Enabled'
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

// Deliver topic events into the queue using the topic's resource identity.
resource subscription 'Microsoft.EventGrid/topics/eventSubscriptions@2024-06-01-preview' = {
  parent: topic
  name: 'to-signals-queue'
  properties: {
    deliveryWithResourceIdentity: {
      identity: {
        type: 'SystemAssigned'
      }
      destination: {
        endpointType: 'ServiceBusQueue'
        properties: {
          resourceId: queue.id
        }
      }
    }
    eventDeliverySchema: 'EventGridSchema'
    retryPolicy: {
      maxDeliveryAttempts: 30
      eventTimeToLiveInMinutes: 1440
    }
  }
  dependsOn: [
    topicSenderAssignment
  ]
}

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
