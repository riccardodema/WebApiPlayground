// =============================================================================
// Modulo: Azure Service Bus (trasporto dell'outbox — PR-2)
// =============================================================================
// Crea un namespace Service Bus + una coda per gli eventi di integrazione
// (arricchimento popolarità). Best practice coerenti col modulo Key Vault:
//   - AAD-only: `disableLocalAuth = true` → NIENTE SAS, l'app si autentica con
//     managed identity (DefaultAzureCredential lato app). Nessun segreto nel repo
//     né nei deployment ARM.
//   - RBAC data-plane con role assignment condizionali e idempotenti (name =
//     guid deterministico) → least privilege (Sender + Receiver, non Owner).
//   - diagnostica opzionale verso Log Analytics (come il Key Vault).
//
// NB: questo modulo è scritto e validato con `bicep build` ma — finché non esiste
// un profilo/subscription Azure — NON è ancora stato deployato né verificato con
// `what-if`. Vedi infra/README.md.
// =============================================================================

@minLength(2)
@maxLength(10)
@description('Nome breve del workload, usato nel nome del namespace.')
param workload string

@allowed([
  'dev'
  'prod'
])
@description('Ambiente target.')
param environmentName string

@description('Region Azure.')
param location string = resourceGroup().location

@description('Tag applicati alle risorse.')
param tags object = {}

@description('Nome della coda degli eventi di integrazione (deve combaciare con ServiceBus:QueueName dell\'app).')
param queueName string = 'popularity-enrichment'

@description('Object ID della managed identity dell\'app a cui assegnare Sender+Receiver sulla coda. Vuoto = nessuna assegnazione (assegnabile dopo, quando l\'identità esiste).')
param appPrincipalId string = ''

@minValue(1)
@maxValue(2000)
@description('Tentativi di consegna prima del dead-letter di un messaggio (poison). Deve essere coerente con l\'idempotenza del consumer.')
param maxDeliveryCount int = 10

@description('Resource ID del Log Analytics workspace per i diagnostic log. Vuoto = nessuna diagnostica.')
param diagnosticsWorkspaceId string = ''

// -----------------------------------------------------------------------------
// Naming: il nome del namespace è GLOBALMENTE univoco, 6-50 char. Il token
// derivato è deterministico (stessa sub+RG → stesso nome) → idempotente.
// -----------------------------------------------------------------------------
var nameToken = take(uniqueString(subscription().id, resourceGroup().id), 6)
var namespaceName = take('sb-${workload}-${environmentName}-${nameToken}', 50)

// Built-in role definition IDs del data-plane Service Bus (stabili a livello globale Azure).
// https://learn.microsoft.com/azure/role-based-access-control/built-in-roles#analytics
// Least privilege: l'app PUBBLICA (Sender, dall'outbox) e CONSUMA (Receiver, dal consumer) → entrambi, non Owner.
var roleIds = {
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
}

// -----------------------------------------------------------------------------
// Namespace — SKU Standard (le code richiedono Standard; abilita anche i topic
// per evoluzioni future). `disableLocalAuth` forza l'auth AAD: nessuna SAS.
// -----------------------------------------------------------------------------
resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    // Solo Azure AD / managed identity: le connection string con SAS sono disabilitate
    // (coerente col principio "no SAS" del Key Vault). In locale/test si usa l'emulatore.
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// -----------------------------------------------------------------------------
// Coda degli eventi di integrazione. Le opzioni rispecchiano la semantica
// at-least-once + consumer idempotente del codice (vedi .claude/context/outbox.md):
//   - deadLetteringOnMessageExpiration: un messaggio scaduto va in dead-letter
//     (diagnostica) invece di sparire silenziosamente;
//   - maxDeliveryCount: oltre N tentativi un messaggio "poison" va in dead-letter;
//   - lockDuration: finestra di elaborazione prima che il messaggio torni visibile.
// -----------------------------------------------------------------------------
resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBusNamespace
  name: queueName
  properties: {
    maxDeliveryCount: maxDeliveryCount
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    // Idempotenza lato consumer: niente duplicate-detection lato broker (ridondante e costoso).
    requiresDuplicateDetection: false
    requiresSession: false
  }
}

// -----------------------------------------------------------------------------
// RBAC — assegnazioni condizionali e idempotenti sull'AMBITO della coda (non
// dell'intero namespace) → least privilege. Saltate se il principal è vuoto.
// -----------------------------------------------------------------------------
resource senderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appPrincipalId)) {
  name: guid(queue.id, appPrincipalId, roleIds.serviceBusDataSender)
  scope: queue
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.serviceBusDataSender)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource receiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appPrincipalId)) {
  name: guid(queue.id, appPrincipalId, roleIds.serviceBusDataReceiver)
  scope: queue
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.serviceBusDataReceiver)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Diagnostica: invia metriche e log operativi al workspace. Condizionale:
// creata solo se è fornito un workspace. Vedi infra/docs/monitoring.md.
// -----------------------------------------------------------------------------
#disable-next-line use-recent-api-versions
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  name: 'to-log-analytics'
  scope: serviceBusNamespace
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// -----------------------------------------------------------------------------
// Output — solo identificatori, nessun segreto (niente connection string: l'app
// usa il FQDN + managed identity).
// -----------------------------------------------------------------------------
output namespaceName string = serviceBusNamespace.name
output fullyQualifiedNamespace string = '${serviceBusNamespace.name}.servicebus.windows.net'
output queueName string = queue.name
output id string = serviceBusNamespace.id
