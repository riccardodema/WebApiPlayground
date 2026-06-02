using 'main.bicep'

param environmentName = 'prod'
param workload = 'webapiplay'
param location = 'westeurope'

// I principal ID NON sono committati: passati come override al deploy
// (vedi infra/README.md). Vuoti = RBAC assegnato in un secondo momento.
param adminPrincipalId = ''
param appPrincipalId = ''
