// =============================================================================
// Modulo: Azure Key Vault
// =============================================================================
// Crea un Key Vault con le best practice moderne:
//   - RBAC authorization (NO access policies legacy)
//   - soft-delete (90gg) + purge protection (in prod)
//   - network ACLs con bypass per i servizi Azure
//   - role assignments RBAC condizionali e idempotenti (name = guid deterministico)
//
// NB: il modulo NON crea alcun secret. Il VALORE della connection string viene
// impostato fuori dall'IaC (vedi infra/README.md) così nessun segreto transita
// mai per i deployment ARM né finisce nel repo.
// =============================================================================

@minLength(2)
@maxLength(10)
@description('Nome breve del workload, usato nel nome del vault.')
param workload string

@allowed([
  'dev'
  'prod'
])
@description('Ambiente target.')
param environmentName string

@description('Region Azure.')
param location string = resourceGroup().location

@description('Tag applicati al vault.')
param tags object = {}

@description('Object ID a cui assegnare "Key Vault Secrets Officer". Vuoto = nessuna assegnazione.')
param adminPrincipalId string = ''

@description('Object ID a cui assegnare "Key Vault Secrets User". Vuoto = nessuna assegnazione.')
param appPrincipalId string = ''

@description('Abilita la purge protection. Una volta attiva NON è più disattivabile.')
param enablePurgeProtection bool = true

@allowed([
  'standard'
  'premium'
])
@description('SKU del Key Vault.')
param skuName string = 'standard'

// -----------------------------------------------------------------------------
// Naming: il nome del Key Vault è GLOBALMENTE univoco, 3-24 char, alfanumerico + '-'.
// Il token derivato è deterministico (stessa sub+RG → stesso nome) → idempotente.
// -----------------------------------------------------------------------------
var nameToken = take(uniqueString(subscription().id, resourceGroup().id), 6)
var keyVaultName = take('kv-${workload}-${environmentName}-${nameToken}', 24)

// Built-in role definition IDs (stabili a livello globale Azure)
// https://learn.microsoft.com/azure/role-based-access-control/built-in-roles
var roleIds = {
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd6'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
}

// -----------------------------------------------------------------------------
// Key Vault
// -----------------------------------------------------------------------------
resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: skuName
    }
    tenantId: subscription().tenantId
    // RBAC al posto delle access policies: gestione accessi centralizzata e auditabile.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    // Mai impostare `false` esplicito: una volta attiva la purge protection non
    // si disabilita più e ARM rifiuterebbe il downgrade → usiamo null quando off.
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      // Demo: 'Allow' per semplicità. In prod stringere a 'Deny' e abilitare
      // solo IP/subnet/Private Endpoint noti (vedi infra/README.md).
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// -----------------------------------------------------------------------------
// RBAC — assegnazioni condizionali e idempotenti
// -----------------------------------------------------------------------------

// Admin/operatore o CI che gestisce i secret (read/write dei valori).
resource adminRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(adminPrincipalId)) {
  name: guid(keyVault.id, adminPrincipalId, roleIds.keyVaultSecretsOfficer)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.keyVaultSecretsOfficer)
    principalId: adminPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Managed identity dell'app: sola lettura dei secret a runtime.
resource appRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appPrincipalId)) {
  name: guid(keyVault.id, appPrincipalId, roleIds.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.keyVaultSecretsUser)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Output
// -----------------------------------------------------------------------------
output name string = keyVault.name
output uri string = keyVault.properties.vaultUri
output id string = keyVault.id
