# Azure Key Vault come config provider

L'app carica i **segreti** (connection string del DB, ecc.) da **Azure Key Vault** all'avvio,
tramite il config provider ufficiale (`Azure.Extensions.AspNetCore.Configuration.Secrets`).
È **config-gated**: con `KeyVault:Uri` vuoto il provider è spento e tutto funziona come prima
(env var / `appsettings.Development.json`). In locale gira contro un **emulatore** in docker
compose; in Azure contro il vault creato dall'IaC ([infra/](../infra/README.md)).

## Perché Azure Key Vault (e non solo env var)

| Problema delle env var / file | Cosa dà Key Vault |
|---|---|
| Il segreto vive in chiaro in `.env`, pipeline, definizione del container, `docker inspect` | Il segreto vive in **un posto solo**, cifrato, fuori dagli artefatti di deploy |
| Chi può leggere l'ambiente legge il segreto | Accesso governato da **RBAC** per-identity (`Key Vault Secrets User`), revocabile |
| Nessuna traccia di chi ha letto cosa | **Audit log** di ogni accesso (Log Analytics, vedi [infra/docs/monitoring.md](../infra/docs/monitoring.md)) |
| Rotazione = ridistribuire l'ambiente ovunque | Rotazione **in un punto**; versioning dei secret; opzionale `ReloadInterval` senza restart |
| Config e segreti mescolati | Separazione netta: la config sta in appsettings/env, **solo i segreti** nel vault |

Principio guida (2026): **secretless dove possibile, Key Vault per ciò che resta.**
Il segreto migliore è quello che non esiste — Service Bus usa managed identity (niente SAS),
e se il DB migrasse ad Azure SQL con auth Entra anche la connection string sparirebbe dal vault.

## Cosa sta nel vault (e cosa no)

| Valore | Nel vault? | Perché |
|---|---|---|
| `ConnectionStrings--Default` (DB) | ✅ | Contiene una password: è un segreto. |
| `ServiceBus--ConnectionString` | ✅ solo in locale (emulatore, SAS obbligatoria) | In Azure **non esiste**: managed identity + `ServiceBus:FullyQualifiedNamespace` (un hostname non è un segreto). |
| `AzureAd:ClientId/TenantId/Audience` | ❌ | Identificatori pubblici, non segreti → restano app settings. Metterli nel vault diluisce il segnale ("tutto è segreto" = niente lo è). |
| `KeyVault:Uri` stesso | ❌ (ovviamente) | È l'indirizzo del vault: serve *prima* di poterlo leggere. |

Naming: `--` nel nome del secret = `:` nella configuration .NET
(`ConnectionStrings--Default` → `ConnectionStrings:Default`). Convenzione del provider ufficiale.

## Come funziona nel codice

[`KeyVaultConfigurationExtensions`](../src/WebApiPlayground.Api/Configuration/KeyVault/KeyVaultConfigurationExtensions.cs)
(layer Api: il bootstrap della configurazione è della composition root dell'host — regola NetArchTest)
viene chiamato in testa a `Program.cs`, **prima** del fail-fast di
`StartupConfigurationValidator`: i secret del vault entrano in `IConfiguration` e soddisfano il
validator. Tre proprietà chiave:

1. **Precedenza**: il provider Key Vault è aggiunto per **ultimo** → i secret del vault **vincono**
   su appsettings ed env var (precedenza standard di `IConfiguration`).
2. **Credential esplicita per ambiente** (`KeyVault:Credential`) — niente `DefaultAzureCredential`:
   la catena implicita è lenta allo startup, non deterministica e dà errori opachi (guidance Azure
   SDK). I valori:
   - `ManagedIdentity` (default) — in Azure; `ManagedIdentityClientId` per la user-assigned.
   - `AzureCli` — run locale contro il vault **reale** (richiede `az login`).
   - `Emulator` — **solo Development** (fail-fast altrove): accetta il TLS self-signed
     dell'emulatore e minta localmente un token fittizio.
3. **Fail-fast parlante**: se `KeyVault:Uri` è valorizzato ma il vault non è
   raggiungibile/autorizzato, l'app **non parte** e il log `Fatal` dice *dove* puntava, *con che
   credential*, le *cause probabili* per quel tipo di errore e il *rimedio* (vedi
   [Troubleshooting](#troubleshooting)). Exit code **non-zero** → orchestratore e CI se ne accorgono.

Config (sezione `KeyVault` in `appsettings.json`):

```jsonc
{
  "KeyVault": {
    "Uri": "",                       // vuoto = provider spento; es. https://kv-webapiplay-dev-xxxxxx.vault.azure.net/
    "Credential": "ManagedIdentity", // ManagedIdentity | AzureCli | Emulator (solo Development)
    "ManagedIdentityClientId": "",   // solo per user-assigned MI
    "ReloadInterval": ""             // es. "00:05:00": rilegge i secret a intervalli (rotazione senza restart)
  }
}
```

## Run locale: docker compose (emulatore)

`docker compose up` include l'**emulatore Key Vault**
([james-gould/azure-keyvault-emulator](https://github.com/james-gould/azure-keyvault-emulator)):
stessa API REST e stesso SDK del cloud. Le connection string **non stanno più nell'env dell'api**:

```
keyvault-certs (one-shot)  →  genera il cert TLS self-signed nel volume
keyvault                   →  emulatore (https://keyvault:4997, immagine pinnata 3.1.0)
keyvault-seed (one-shot)   →  PUT dei secret via REST (DB + Service Bus connection string)
api                        →  parte DOPO il seed; KeyVault__Uri + KeyVault__Credential=Emulator
```

Ispezione dall'host: `curl -k https://localhost:4997/secrets?api-version=7.4 -H "Authorization: Bearer <jwt qualsiasi ben formato>"`.

### Perché un emulatore community, e con quali cautele

Microsoft non pubblica un emulatore Key Vault ufficiale (a differenza di Storage/Cosmos/Service
Bus); questo è il più adottato nell'ecosistema .NET (integrato in Aspire). Cautele applicate qui:

- **Mai in produzione**: la credential `Emulator` è rifiutata fuori da Development (fail-fast).
- **Mai segreti reali**: contiene solo i valori dev che prima stavano in chiaro in `.env`.
- **Immagine pinnata** (`3.1.0`, niente `:latest`); tag `3.1.0-arm` nativo per Apple Silicon
  (vedi `.env.example`).
- **Nessun NuGet del progetto emulatore**: il loro pacchetto installerebbe una CA self-signed nel
  trust store dell'host. Qui il trust del TLS self-signed è **scoped al solo client** in modalità
  Emulator, e il token è mintato localmente (l'emulatore non valida firma/claim).

## Run contro il vault reale (quando colleghi la subscription)

```bash
# 1. Crea l'infrastruttura (Key Vault incluso; whatif prima di deploy)
AZURE_SUBSCRIPTION_ID='<sub>' ./infra/deploy.sh deploy
#    → output: keyVaultUri (e keyVaultName)
#    Per scrivere i secret ti serve il ruolo 'Key Vault Secrets Officer' (param adminPrincipalId)
#    e il tuo IP nel firewall (param allowedIpAddresses) — il default è Deny.

# 2. Metti i segreti nel vault (valore da prompt silenzioso: mai in argv/history)
AZURE_SUBSCRIPTION_ID='<sub>' ./infra/set-secrets.sh ConnectionStrings--Default

# 3a. Run LOCALE contro il vault reale (az login già fatto)
KeyVault__Uri='https://kv-webapiplay-dev-xxxxxx.vault.azure.net/' \
KeyVault__Credential='AzureCli' \
dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj

# 3b. In Azure (App Service / Container Apps): managed identity
#     - assegna l'identity all'app e passala come appPrincipalId al deploy Bicep
#       (→ ruolo 'Key Vault Secrets User' sul vault, già nel modulo keyvault.bicep)
#     - app settings: KeyVault__Uri=<keyVaultUri>   (Credential resta il default ManagedIdentity)
```

Alternativa di piattaforma: gli App Service **Key Vault references**
(`@Microsoft.KeyVault(SecretUri=...)`) risolvono i secret *fuori* dall'app. Il provider in-app
scelto qui è portabile (container, AKS, locale), supporta `ReloadInterval` e rende l'app
autosufficiente; le KV references restano possibili proprio perché il provider è config-gated.

## Test (cosa garantisce che funzioni sul vault vero)

| Livello | Dove | Cosa copre |
|---|---|---|
| Unit | `tests/WebApiPlayground.Tests/Configuration/KeyVaultConfigurationTests.cs` | config-gating, URI non-https, scelta credential (case-insensitive), guard Emulator fuori da Development, messaggi di errore parlanti per 403 / credential / rete |
| Integration (emulatore via Testcontainers) | `tests/WebApiPlayground.IntegrationTests/KeyVault/` | **e2e reale**: app avviata con la connection string *solo* nel vault → risponde dal DB; mapping `--`→`:`; i secret del vault vincono su appsettings; fail-fast con vault irraggiungibile |
| Contract (statici) | `tests/WebApiPlayground.DockerTests/` | compose: niente segreti nell'env dell'api, immagine emulatore pinnata, api parte dopo il seed, script cert/seed coerenti |
| Architecture | `tests/WebApiPlayground.ArchitectureTests/` | SDK Key Vault confinato al layer Api |

L'integrazione usa **la stessa meccanica** del vault reale (stesso provider, stesso SDK, stessa
semantica REST): collegare la subscription cambia URI e credential, non il codice — e il percorso
managed identity/RBAC è coperto dal fail-fast parlante se manca qualcosa.

## Troubleshooting

L'app non parte e il log `Fatal` dice... | Causa e rimedio
---|---
`Configurazione obbligatoria mancante ... KeyVault:Uri` (hint in coda) | Fuori da Development mancano i segreti: forniscili via env var **oppure** imposta `KeyVault__Uri` (il vault li fornisce lui). |
`... non è un URI https assoluto` | `KeyVault__Uri` malformato. Usa l'output `keyVaultUri` del deploy (`https://<nome>.vault.azure.net/`). |
`Credential = 'Emulator' non è consentito nell'ambiente '...'` | L'emulatore è solo per Development: in Azure usa `ManagedIdentity`, in locale contro il vault vero `AzureCli`. |
`Impossibile caricare i secret ... NON ha il ruolo RBAC` (403) | L'identity è autenticata ma senza `Key Vault Secrets User`: passa l'object id come `appPrincipalId` al deploy, o crea il role assignment a mano. |
`Impossibile caricare i secret ... managed identity non è disponibile` | Stai girando fuori da Azure (niente IMDS) o l'identity non è assegnata. In locale usa `AzureCli`. |
`Impossibile caricare i secret ... az login` | Sessione Azure CLI assente/scaduta: `az login` (+ `az account set`). |
`Impossibile caricare i secret ...` (rete/timeout) | URI sbagliato, oppure **firewall default-deny** del vault: aggiungi il tuo IP in `allowedIpAddresses` e rideploya. In compose: emulatore non partito (`docker compose logs keyvault`). |

Per **spegnere** il provider e avviare comunque (segreti via env var): `KeyVault__Uri=""`.
