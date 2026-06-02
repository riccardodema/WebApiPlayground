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
    // Purge protection sempre attiva (best practice): protegge i secret da
    // cancellazioni definitive accidentali. Una volta attiva non è disabilitabile.
    enablePurgeProtection: true
  }
}

// -----------------------------------------------------------------------------
// Output
// -----------------------------------------------------------------------------

output resourceGroupName string = resourceGroup.name
output keyVaultName string = keyVault.outputs.name
output keyVaultUri string = keyVault.outputs.uri
