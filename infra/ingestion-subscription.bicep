// ingestion-subscription.bicep — PHASE 2 (apply AFTER main.bicep).
//
// Creates the Event Grid → Service Bus queue event subscription with identity-
// based delivery. This is split out of main.bicep because Event Grid validates
// managed-identity delivery synchronously at creation, racing RBAC propagation of
// the topic's Service Bus Data Sender role assignment (aka.ms/egmsivalidation).
// By the time you run this (after main.bicep completes), the role has propagated
// and validation succeeds.
//
//   az deployment group create -g <rg> -f infra/ingestion-subscription.bicep \
//     -p topicName=<egt> namespaceName=<sb>

targetScope = 'resourceGroup'

@description('Existing Event Grid custom topic name (from main.bicep output prefix).')
param topicName string

@description('Existing Service Bus namespace name.')
param namespaceName string

@description('Existing Service Bus queue name.')
param queueName string = 'signals'

@description('Event subscription name.')
param subscriptionName string = 'to-signals-queue'

resource topic 'Microsoft.EventGrid/topics@2024-06-01-preview' existing = {
  name: topicName
}

resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: namespaceName
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' existing = {
  parent: namespace
  name: queueName
}

resource subscription 'Microsoft.EventGrid/topics/eventSubscriptions@2024-06-01-preview' = {
  parent: topic
  name: subscriptionName
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
}

@description('The created event subscription resource id.')
output subscriptionId string = subscription.id
