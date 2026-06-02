// =============================================================================
// Modulo: Monitoring (Log Analytics workspace)
// =============================================================================
// Workspace su cui far confluire i log diagnostici delle risorse (es. gli
// AuditEvent del Key Vault). È la destinazione queryabile in KQL su cui poi si
// costruiscono ricerche di audit e alert. Vedi infra/docs/monitoring.md.
// =============================================================================

@minLength(2)
@maxLength(10)
@description('Nome breve del workload, usato nel nome del workspace.')
param workload string

@allowed([
  'dev'
  'prod'
])
@description('Ambiente target.')
param environmentName string

@description('Region Azure.')
param location string = resourceGroup().location

@description('Tag applicati al workspace.')
param tags object = {}

@minValue(30)
@maxValue(730)
@description('Giorni di retention dei log nel workspace.')
param retentionInDays int = 30

// Nome workspace: 4-63 char, alfanumerico + '-'. Token deterministico → idempotente.
var nameToken = take(uniqueString(subscription().id, resourceGroup().id), 6)
var workspaceName = take('log-${workload}-${environmentName}-${nameToken}', 63)

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018' // pay-per-GB: nessun costo fisso, si paga solo l'ingestione
    }
    retentionInDays: retentionInDays
    features: {
      // Accesso ai log governato solo da Azure RBAC sulla risorsa (no chiavi workspace).
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

output id string = workspace.id
output name string = workspace.name
