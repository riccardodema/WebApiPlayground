// =============================================================================
// WebApiPlayground — Infrastructure as Code (entry point)
// =============================================================================
// Deployment a livello di SUBSCRIPTION: crea il resource group (RG come codice)
// e poi delega ai moduli la creazione delle risorse al suo interno.
//
// Idempotente: ARM è dichiarativo. Stesso template + stessi parametri = nessun
// cambiamento (no-op). Usa SEMPRE `what-if` prima di applicare:
//   ./deploy.sh whatif   (anteprima)   →   ./deploy.sh deploy   (applica)
// =============================================================================

targetScope = 'subscription'

// -----------------------------------------------------------------------------
// Parametri
// -----------------------------------------------------------------------------

@minLength(2)
@maxLength(10)
@description('Nome breve del workload, usato nei nomi delle risorse.')
param workload string = 'webapiplay'

@allowed([
  'dev'
  'prod'
])
@description('Ambiente target. Guida naming, purge protection e politiche.')
param environmentName string

@description('Region Azure per tutte le risorse.')
param location string = 'westeurope'

@description('Object ID (principal AAD) a cui dare "Key Vault Secrets Officer" sul vault. Vuoto = skip (es. operatore/CI che gestisce i secret).')
param adminPrincipalId string = ''

@description('Object ID della managed identity dell\'app a cui dare "Key Vault Secrets User". Vuoto = skip (assegnabile dopo, quando l\'App Service esiste).')
param appPrincipalId string = ''

@description('IP/CIDR consentiti dal firewall del Key Vault (vuoto = solo servizi Azure trusted + private endpoint).')
param allowedIpAddresses array = []

@description('Crea un Log Analytics workspace e invia gli audit log del Key Vault. Best practice: lasciare true. Imposta false per non creare risorse di monitoring (es. per azzerare i costi su un ambiente non-live).')
param enableMonitoring bool = true

@description('Crea il namespace Service Bus + coda (trasporto dell\'outbox, PR-2). Imposta false per non creare la risorsa (lo SKU Standard ha un costo fisso) su un ambiente non-live. NB: fuori da Development l\'app fa fail-fast senza ServiceBus:FullyQualifiedNamespace (il fallback in-process vale solo in Development) → spegnilo solo dove l\'app non gira o gira in Development.')
param enableServiceBus bool = true

@description('Nome della coda Service Bus degli eventi di integrazione (deve combaciare con ServiceBus:QueueName dell\'app).')
param serviceBusQueueName string = 'popularity-enrichment'

@minValue(30)
@maxValue(730)
@description('Giorni di retention dei log nel workspace (se enableMonitoring).')
param logRetentionInDays int = 30

@description('Tag comuni applicati a tutte le risorse.')
param tags object = {
  workload: workload
  environment: environmentName
  managedBy: 'bicep'
}

// -----------------------------------------------------------------------------
// Resource group (anch'esso codice/versionato)
// -----------------------------------------------------------------------------

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: 'rg-${workload}-${environmentName}'
  location: location
  tags: tags
}

// -----------------------------------------------------------------------------
// Moduli
// -----------------------------------------------------------------------------

module monitoring 'modules/monitoring.bicep' = if (enableMonitoring) {
  scope: resourceGroup
  name: 'monitoring'
  params: {
    workload: workload
    environmentName: environmentName
    location: location
    tags: tags
    retentionInDays: logRetentionInDays
  }
}

module keyVault 'modules/keyvault.bicep' = {
  scope: resourceGroup
  name: 'keyvault'
  params: {
    workload: workload
    environmentName: environmentName
    location: location
    tags: tags
    adminPrincipalId: adminPrincipalId
    appPrincipalId: appPrincipalId
    allowedIpAddresses: allowedIpAddresses
    // Invia gli audit log al workspace se il monitoring è attivo.
    diagnosticsWorkspaceId: enableMonitoring ? monitoring!.outputs.id : ''
    // Purge protection sempre attiva (best practice): protegge i secret da
    // cancellazioni definitive accidentali. Una volta attiva non è disabilitabile.
    enablePurgeProtection: true
  }
}

module serviceBus 'modules/servicebus.bicep' = if (enableServiceBus) {
  scope: resourceGroup
  name: 'servicebus'
  params: {
    workload: workload
    environmentName: environmentName
    location: location
    tags: tags
    queueName: serviceBusQueueName
    // Stessa managed identity dell'app (Key Vault Secrets User → qui Sender+Receiver sulla coda).
    appPrincipalId: appPrincipalId
    // Invia metriche/log al workspace se il monitoring è attivo.
    diagnosticsWorkspaceId: enableMonitoring ? monitoring!.outputs.id : ''
  }
}

// -----------------------------------------------------------------------------
// Output
// -----------------------------------------------------------------------------

output resourceGroupName string = resourceGroup.name
output keyVaultName string = keyVault.outputs.name
output keyVaultUri string = keyVault.outputs.uri
// Service Bus: vuoti se non creato. Il FQDN va in ServiceBus:FullyQualifiedNamespace dell'app (auth via managed identity).
output serviceBusNamespaceName string = enableServiceBus ? serviceBus!.outputs.namespaceName : ''
output serviceBusFullyQualifiedNamespace string = enableServiceBus ? serviceBus!.outputs.fullyQualifiedNamespace : ''
output serviceBusQueueName string = enableServiceBus ? serviceBus!.outputs.queueName : ''
