# Monitoring & Diagnostics del Key Vault — guida

> Documento di riferimento: spiega **cosa significa** e **a cosa serve** il monitoring
> aggiunto all'IaC, così è facile ricordarselo a distanza di tempo. Implementazione:
> [`modules/monitoring.bicep`](../modules/monitoring.bicep) + le diagnostic settings in
> [`modules/keyvault.bicep`](../modules/keyvault.bicep).

## Il problema che risolve

Un Key Vault custodisce i segreti (es. la connection string del DB). Di default,
**chi accede a quei segreti è invisibile**: non c'è traccia di *chi* ha letto un secret,
*quando*, *da dove* e con *quale esito*. Se un domani sospetti che una credenziale sia
trapelata, senza log non puoi rispondere alla domanda più importante: «**chi ha letto
questo secret e quando?**».

Il monitoring colma questa lacuna registrando in modo permanente e queryabile ogni
operazione sul Key Vault.

## I concetti, in breve

### Diagnostic settings
Sono un "rubinetto" che si attacca a una risorsa Azure e ne fa **colare la telemetria**
verso una destinazione. Non producono nulla da sole: dicono *quali categorie* di log/metriche
inviare e *dove*. Sono una *extension resource* (vivono "sopra" la risorsa che osservano —
qui il Key Vault).

### Categorie di log del Key Vault
- **AuditEvent** (categoria `audit`) — il cuore della sicurezza: **un record per ogni accesso**
  al piano dati (secret/chiavi/certificati). Contiene identità del chiamante, operazione
  (es. `SecretGet`), risorsa toccata, IP di origine, timestamp ed esito (200 / 403 / 429).
- **AllMetrics** — metriche operative: disponibilità, latenza, volume di chiamate, throttling.

> Usiamo il **category group `audit`** invece di elencare le singole categorie: è il modo
> moderno e a prova di futuro (se Azure aggiunge categorie di audit, sono incluse in automatico).

### Log Analytics workspace
È la **destinazione** dei log: un archivio gestito, interrogabile con **KQL** (Kusto Query
Language). Una volta che gli AuditEvent confluiscono qui, puoi fare ricerche, dashboard e
**alert**. Esiste anche l'opzione Storage Account (archivio economico, ma senza query) o
Event Hub (verso un SIEM); qui usiamo il workspace perché è quello che abilita audit e alert.

- **SKU `PerGB2018`**: niente costo fisso, si paga solo l'ingestione (GB). Su un vault poco
  trafficato è quasi nulla.
- **Retention**: per quanti giorni i log restano queryabili (qui 30gg in dev, 90gg in prod).

## A cosa serve, in pratica

1. **Audit & forensics** — rispondere a «chi ha letto il secret X il giorno Y?».
2. **Compliance** — molti standard (ISO 27001, SOC 2, PCI-DSS) richiedono il log degli
   accessi ai segreti.
3. **Alerting** — costruire allarmi sopra i log (es. picco di `403`, throttling `429`, calo
   di disponibilità).
4. **Best practice / governance** — è la regola `Azure.KeyVault.Logs` di PSRule for Azure:
   con il monitoring attivo, il baseline `Azure.Default` passa **senza esclusioni**.

## Com'è cablato in questo repo

```
main.bicep
  ├─ param enableMonitoring (bool, default true)   ← toggle on/off
  ├─ param logRetentionInDays (default 30; prod=90)
  ├─ module monitoring  = if (enableMonitoring)     → crea il Log Analytics workspace
  └─ module keyVault
        └─ diagnosticsWorkspaceId = enableMonitoring ? monitoring.outputs.id : ''
              └─ diagnosticSettings 'audit-to-log-analytics' = if (workspaceId != '')
                    → invia categoryGroup 'audit' + AllMetrics al workspace
```

- **Attivo di default** (best practice). Le risorse di monitoring sono **condizionali**: con
  `enableMonitoring = false` non vengono create (workspace e diagnostic settings spariscono),
  utile per azzerare i costi su un ambiente non-live.
- Tutto resta **idempotente**: nomi deterministici, ri-deploy = no-op.

## Abilitare / disabilitare

Già attivo nei `*.bicepparam`. Per spegnerlo in un deploy:

```bash
# via override al deploy
PARAMS="enableMonitoring=false" AZURE_SUBSCRIPTION_ID=<sub> ./infra/deploy.sh deploy
```

> Con `enableMonitoring = false` la regola PSRule `Azure.KeyVault.Logs` tornerebbe a fallire:
> è il trade-off costo ↔ best practice, esplicito e reversibile.

## Costi (orientativi)

- Il workspace non ha **canone fisso** (SKU `PerGB2018`): paghi solo i **GB ingeriti** e la
  retention oltre il periodo incluso. Un Key Vault a basso traffico genera pochissimi log.
- Finché l'IaC **non è deployato** su Azure (caso attuale), il costo è **zero**: i template
  esistono solo come codice.

## Verificare dopo un deploy reale

```bash
# le diagnostic settings esistono sul vault
az monitor diagnostic-settings list --resource <keyVaultResourceId> -o table
```

Esempi di query KQL nel workspace (portale → Logs):

```kql
// Chi ha letto i secret nelle ultime 24h
AzureDiagnostics
| where ResourceType == "VAULTS" and OperationName == "SecretGet"
| where TimeGenerated > ago(24h)
| project TimeGenerated, identity_claim_upn_s, CallerIPAddress, id_s, ResultSignature
| order by TimeGenerated desc
```

```kql
// Accessi negati (possibili tentativi non autorizzati)
AzureDiagnostics
| where ResourceType == "VAULTS" and ResultSignature == "Forbidden"
| summarize count() by identity_claim_upn_s, CallerIPAddress, bin(TimeGenerated, 1h)
```

## Test automatici

- `tests/WebApiPlayground.IacTests` — `MonitoringModuleTests` (il workspace è `PerGB2018`,
  retention configurabile, nome deterministico) e `KeyVaultModuleTests.Audit_diagnostics_*`
  (le diagnostic settings inviano `audit` al workspace, in modo condizionale).
- **PSRule for Azure** — con il monitoring attivo, `Azure.KeyVault.Logs` passa. Resta una sola
  esclusione documentata, `Azure.Log.Replication` (replica cross-region del workspace = DR di
  scala, fuori scopo per un ambiente non-live).

## Glossario rapido

| Termine | Significato |
|---|---|
| **Diagnostic settings** | Config che instrada log/metriche di una risorsa verso una destinazione. |
| **AuditEvent / `audit`** | Categoria di log con un record per ogni accesso al piano dati del Key Vault. |
| **Log Analytics workspace** | Archivio queryabile (KQL) dei log; base per ricerche e alert. |
| **KQL** | Kusto Query Language, il linguaggio di query dei log Azure. |
| **PerGB2018** | SKU pay-per-GB del workspace: nessun canone fisso. |
| **Retention** | Giorni per cui i log restano interrogabili. |
