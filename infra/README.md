# Infrastructure as Code — Bicep

L'infrastruttura Azure è **versionata nel repo** come [Bicep](https://learn.microsoft.com/azure/azure-resource-manager/bicep/),
allo stesso modo in cui lo schema del DB è versionato come SQL project (vedi
[`database/`](../database/)). Dichiarativa, **idempotente**, rilasciabile a ogni push
senza rompere se lo stato non cambia, con **what-if** come anteprima obbligatoria e
**test** automatici (build/lint + PSRule for Azure).

Si parte dal **Key Vault**, dove in produzione vivrà la connection string del DB.

## Struttura

```
infra/
├── main.bicep                 # entry point — targetScope='subscription': crea il RG e invoca i moduli
├── main.dev.bicepparam        # parametri ambiente dev
├── main.prod.bicepparam       # parametri ambiente prod
├── modules/
│   ├── keyvault.bicep         # Key Vault (RBAC, soft-delete, purge protection, firewall, diagnostics)
│   ├── monitoring.bicep       # Log Analytics workspace (destinazione degli audit log)
│   └── servicebus.bicep       # Service Bus namespace + coda (trasporto outbox, PR-2 — AAD-only, RBAC, no SAS)
├── docs/
│   └── monitoring.md          # guida: a cosa serve il monitoring/diagnostics del Key Vault
├── tests/
│   ├── ps-rule.yaml           # config PSRule for Azure (asserzioni best-practice)
│   └── install-bicep.sh       # scarica la Bicep CLI in .tools/ per i test locali
├── bicepconfig.json           # regole del linter Bicep
└── deploy.sh                  # wrapper locale: build/lint → what-if (default) | deploy
```

## Scelte architetturali

| Scelta | Perché |
|---|---|
| **Scope = subscription** | `main.bicep` crea anche il **resource group**: anche il RG è codice/tracciato. |
| **RBAC authorization** (`enableRbacAuthorization: true`) | Niente access policies legacy: accessi gestiti via Azure RBAC, auditabili e centralizzati. |
| **Soft-delete + purge protection** | Soft-delete 90gg e **purge protection sempre attiva** (protegge i secret da cancellazioni definitive). Una volta attiva non è disabilitabile. |
| **Firewall default-deny** | `networkAcls.defaultAction = 'Deny'` + bypass per i servizi Azure trusted. Aggiungi IP/CIDR con il parametro `allowedIpAddresses` (o un Private Endpoint) per l'accesso amministrativo. |
| **Nessun secret nell'IaC** | L'IaC crea solo il vault e gli accessi RBAC. Il **valore** della connection string è impostato fuori dall'IaC → nessun segreto transita per i deployment ARM né finisce nel repo. |
| **Monitoring/audit attivo** | Un Log Analytics workspace + diagnostic settings inviano gli **AuditEvent** del vault (chi accede ai secret). Attivo di default (`enableMonitoring`), spegnibile per azzerare i costi. Vedi [docs/monitoring.md](docs/monitoring.md). |
| **Service Bus AAD-only** | Namespace Standard + coda per l'outbox (PR-2): `disableLocalAuth: true` (**niente SAS**), RBAC data-plane least-privilege (Data Sender + Receiver sull'ambito **coda**, non Owner). L'app si autentica con managed identity. Attivo di default (`enableServiceBus`), spegnibile (SKU Standard a costo fisso). Vedi [sezione sotto](#service-bus--trasporto-delloutbox-pr-2). |
| **Naming deterministico** | Nomi globale-unici con token da `uniqueString(sub, rg)` (KV ≤24 char, SB namespace ≤50): stesso input → stesso nome → idempotente. |

## Prerequisiti

- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az`) — installa la Bicep CLI on-demand (`az bicep install`).
- Per il deploy: principal con un ruolo a livello **subscription** (es. `Contributor` + `User Access Administrator`, o `Owner`) per poter creare il RG e assegnare RBAC sul vault.

## Comandi

```bash
# Anteprima (what-if): NON applica nulla, mostra il diff Create/Modify/Delete/NoChange
AZURE_SUBSCRIPTION_ID=<sub-guid> ./infra/deploy.sh            # default = whatif
AZURE_SUBSCRIPTION_ID=<sub-guid> ENV=prod ./infra/deploy.sh   # what-if su prod

# Applica (idempotente)
AZURE_SUBSCRIPTION_ID=<sub-guid> ./infra/deploy.sh deploy

# Override parametri non committati (es. principal ID per l'RBAC)
PARAMS="adminPrincipalId=<objId> appPrincipalId=<objId>" \
  AZURE_SUBSCRIPTION_ID=<sub-guid> ./infra/deploy.sh deploy
```

Variabili: `AZURE_SUBSCRIPTION_ID` (obbligatoria), `AZURE_LOCATION` (default `westeurope`), `ENV` (`dev`|`prod`, default `dev`).

### Solo build/lint (offline, senza Azure)

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build-params --file infra/main.dev.bicepparam --stdout > /dev/null
```

## What-if e idempotenza

ARM è **dichiarativo**: applicare due volte lo stesso template con gli stessi parametri
non produce cambiamenti (no-op). I `roleAssignments` usano un `name` derivato da
`guid(...)` deterministico, quindi non vengono mai duplicati.

`what-if` è l'**anteprima obbligatoria** prima di ogni `deploy` — l'equivalente IaC del
`./database/deploy.sh script` per il DB. `deploy.sh` ha `whatif` come default proprio per
questo: si applica solo dopo aver letto il diff.

## Test

Due livelli, entrambi senza Azure né Docker (l'equivalente "integration" è il `what-if`,
che richiede una subscription reale ed è gated nella pipeline di deploy):

### 1. Unit test xUnit — `tests/WebApiPlayground.IacTests`

Compilano i template Bicep in ARM e asseriscono le scelte **specifiche** di questo progetto
(RBAC abilitato, soft-delete 90gg, purge protection mai hardcoded a `false`, firewall default-deny,
nome KV ≤24 char, nessun secret creato dall'IaC, role assignment condizionali e deterministici).
Stesso stack del resto del repo (xUnit + `dotnet test`).

**Eseguirli in locale** — serve la Bicep CLI. Il modo più semplice (nessun Azure CLI, nessun
PATH da toccare): scaricala una volta nella cartella locale del repo, poi `dotnet test`:

```bash
./infra/tests/install-bicep.sh    # scarica .tools/bicep (gitignored); il test la trova da solo
dotnet test tests/WebApiPlayground.IacTests/WebApiPlayground.IacTests.csproj
```

Il test cerca la Bicep CLI in quest'ordine: variabile `BICEP_CLI_PATH` → `.tools/bicep` →
`~/.azure/bin/bicep` (da `az bicep install`) → `bicep`/`az` sul PATH. Se non la trova, i test si
**SKIPpano** (non falliscono).

### 2. PSRule for Azure — `tests/ps-rule.yaml`

[PSRule for Azure](https://azure.github.io/PSRule.Rules.Azure/) espande i Bicep in ARM e applica
centinaia di regole best-practice **generiche** (baseline `Azure.Default`). Complementare agli
unit test sopra. `Azure.KeyVault.Logs` è ora soddisfatta dal modulo monitoring (vedi
[docs/monitoring.md](docs/monitoring.md)). Unica esclusione documentata: `Azure.Log.Replication`
(replica cross-region del workspace = disaster recovery di scala, fuori scopo per un ambiente
non-live — vedi `tests/ps-rule.yaml`).

```powershell
Install-Module PSRule.Rules.Azure -Scope CurrentUser   # richiede PowerShell
Assert-PSRule -InputPath ./infra/ -Option ./infra/tests/ps-rule.yaml
```

Entrambi girano automaticamente nei workflow `infra` (GitHub Actions e Azure DevOps).

## CI/CD

Doppia implementazione, come il resto del progetto:

| | GitHub Actions | Azure DevOps |
|---|---|---|
| File | [`.github/workflows/infra.yml`](../.github/workflows/infra.yml) | [`.azure/pipelines/infra.yml`](../.azure/pipelines/infra.yml) |
| Su PR (`infra/**`) | job `validate`: build/lint + PSRule | stage `Validate` |
| Su `main` | `validate` → `deploy` (what-if → create) | `Validate` → `Deploy` |
| Gate deploy | environment `production` + variabile `AZURE_LOCATION` | environment `production` |

**Disabled-until-configured** (stesso pattern del deploy app): senza la variabile
`AZURE_LOCATION` il job `deploy` è **SKIPPED** (non failed) → CI verde, zero chiamate ad
Azure. Appostala (più i secret OIDC `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`
e l'environment `production`) per attivare il deploy reale. Setup OIDC: vedi
[.github/workflows/README.md](../.github/workflows/README.md).

## Impostare il valore del secret (fuori dall'IaC)

L'IaC crea il vault ma **non** il valore della connection string. Una volta deployato il
vault, impostalo con l'identità che ha il ruolo *Key Vault Secrets Officer*:

```bash
az keyvault secret set \
  --vault-name <nome-key-vault> \
  --name Sql-ConnectionString \
  --value 'Server=tcp:...;Authentication=Active Directory Default;Database=...;'
```

> Il firewall è **default-deny**: per scrivere il secret devi essere su un IP consentito
> (`allowedIpAddresses`), dietro un Private Endpoint, o eseguire da un contesto Azure trusted.

## Service Bus — trasporto dell'outbox (PR-2)

Il modulo [`modules/servicebus.bicep`](modules/servicebus.bicep) crea un **namespace Service Bus** (SKU Standard) e
una **coda** (`popularity-enrichment`) usata come trasporto dell'outbox: l'app pubblica gli eventi di integrazione
e un consumer disaccoppiato li elabora (vedi [`.claude/context/outbox.md`](../.claude/context/outbox.md), sez. PR-2).

| Scelta | Perché |
|---|---|
| **AAD-only** (`disableLocalAuth: true`) | Niente connection string SAS: l'app si autentica con **managed identity** (`DefaultAzureCredential`). Stesso principio "no segreti" del Key Vault. |
| **RBAC sull'ambito coda** | Least-privilege: l'app riceve *Azure Service Bus Data Sender* (pubblica) + *Data Receiver* (consuma), **non** Owner. Assegnazioni condizionali (saltate se `appPrincipalId` è vuoto) e idempotenti (`name = guid(...)`). |
| **Dead-lettering** | `maxDeliveryCount` + `deadLetteringOnMessageExpiration`: un messaggio "poison" finisce in dead-letter (diagnostica) invece di sparire o ciclare all'infinito. Coerente col consumer **idempotente** lato app. |
| **Toggle `enableServiceBus`** | Lo SKU Standard ha un costo fisso: spegnibile su ambienti non-live (come `enableMonitoring`). ⚠️ Fuori da Development l'app **richiede** `ServiceBus__FullyQualifiedNamespace` (fail-fast allo startup): il fallback in-process vale **solo in Development** → spegnilo solo dove l'app non gira o gira in Development. |

L'app si collega passando il **FQDN** in output (`serviceBusFullyQualifiedNamespace`) all'app setting
`ServiceBus__FullyQualifiedNamespace` (+ `ServiceBus__QueueName`): nessuna connection string, auth via managed
identity. In locale/test si usa invece l'**emulatore** ufficiale (connection string SAS), non questo namespace.

> ⚠️ **Stato:** il modulo è **scritto e validato** con `bicep build` + test IaC (`ServiceBusModuleTests`) e provato
> **end-to-end con l'emulatore** ASB (vedi i test di integrazione), ma — finché non esiste un profilo/subscription
> Azure — **non è ancora stato deployato né verificato con `what-if`** contro un namespace reale. È un passo da fare
> alla creazione dell'account (insieme al deploy del Key Vault e al config provider).

## Integrazione con l'App Service (passo successivo)

End-state per togliere la connection string anche dalla pipeline `cd.yml`/`ci-cd.yml`:

1. L'App Service ha una **system-assigned managed identity**.
2. Il suo `principalId` viene passato all'IaC come `appPrincipalId` → ottiene il ruolo
   **Key Vault Secrets User** (sola lettura).
3. L'app setting usa una **Key Vault reference** invece del valore in chiaro:

   ```
   ConnectionStrings__Default = @Microsoft.KeyVault(SecretUri=https://<kv>.vault.azure.net/secrets/Sql-ConnectionString)
   ```

Così il secret non compare né nel repo né nelle pipeline: l'app lo legge a runtime dal
Key Vault tramite la sua identità. La ri-cablatura del CD è un passo successivo a questa
foundation.
