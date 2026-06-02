using 'main.bicep'

param environmentName = 'dev'
param workload = 'webapiplay'
param location = 'westeurope'

// I principal ID NON sono committati: vengono passati al deploy come override
// (es. `az deployment sub create ... --parameters adminPrincipalId=<objectId>`)
// oppure lasciati vuoti per assegnare l'RBAC in un secondo momento.
param adminPrincipalId = ''
param appPrincipalId = ''
