using 'main.bicep'

param environmentName = 'prod'
param workload = 'webapiplay'
param location = 'westeurope'

// In prod tieni gli audit log più a lungo (in dev resta il default di 30 giorni).
param logRetentionInDays = 90

// I principal ID NON sono committati: passati come override al deploy
// (vedi infra/README.md). Vuoti = RBAC assegnato in un secondo momento.
param adminPrincipalId = ''
param appPrincipalId = ''
