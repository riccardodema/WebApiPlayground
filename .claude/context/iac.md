# Infrastructure as Code — Bicep

L'infrastruttura Azure è versionata in `infra/` come **Bicep**, dichiarativa e
**idempotente**. Foundation attuale: **Azure Key Vault**. Documento completo per
l'utente: `infra/README.md`.

## Struttura

```
infra/
├── main.bicep              # entry point, targetScope='subscription' (crea RG + invoca moduli)
├── main.dev.bicepparam     # param ambiente dev
├── main.prod.bicepparam    # param ambiente prod
├── modules/keyvault.bicep  # Key Vault (RBAC, soft-delete, purge, role assignments condizionali)
├── tests/ps-rule.yaml      # PSRule for Azure (baseline Azure.Default)
├── bicepconfig.json        # regole linter
└── deploy.sh               # wrapper: whatif (default) | deploy

tests/WebApiPlayground.IacTests/   # unit test xUnit: compila il Bicep in ARM e asserisce
```

## Test (due livelli, no Azure/Docker)

1. **xUnit** `tests/WebApiPlayground.IacTests` — compila i Bicep in ARM (via `bicep`/`az bicep`)
   e asserisce le scelte specifiche (RBAC, soft-delete 90gg, purge protection mai `false`, nome
   KV ≤24, nessun secret, role assignment condizionali/deterministici). Si SKIPpa se la Bicep CLI
   manca (`Xunit.SkippableFact`). `dotnet test tests/WebApiPlayground.IacTests/...`.
2. **PSRule for Azure** (`tests/ps-rule.yaml`) — regole best-practice generiche, baseline `Azure.Default`.

Equivalente "integration" = `what-if` (richiede subscription reale, gated nel deploy).
Entrambi girano nei workflow `infra` (GH + ADO).

## Regole

- **Scope = subscription**: `main.bicep` crea anche il resource group. I moduli girano
  con `scope: <rg>`.
- **RBAC, non access policies**: `enableRbacAuthorization: true`. Gli accessi sono
  `roleAssignments` con `name: guid(...)` deterministico (idempotenti, mai duplicati).
- **Purge protection solo in prod**: `enablePurgeProtection: environmentName == 'prod'`.
  Mai impostare `false` esplicito → una volta attiva non si disabilita (usare `null`).
- **Nessun secret nell'IaC**: si crea solo il vault + RBAC. Il valore della connection
  string si imposta fuori (`az keyvault secret set`). Nessun segreto transita per ARM.
- **Naming KV**: globale-unico ≤24 char, `take('kv-${workload}-${env}-${take(uniqueString(sub,rg),6)}', 24)`.
- **what-if obbligatorio** prima di `deploy` (anteprima del diff). `deploy.sh` default = `whatif`.

## Built-in role definition IDs (Key Vault)

| Ruolo | ID |
|---|---|
| Key Vault Secrets Officer (read/write secret) | `b86a8fe4-44ce-4948-aee5-eccb2c155cd6` |
| Key Vault Secrets User (read secret) | `4633458b-17de-408a-b874-0445c86b69e6` |

## Comandi

```bash
# Build/lint offline (no Azure)
az bicep build --file infra/main.bicep --stdout > /dev/null

# What-if (anteprima) / deploy (idempotente)
AZURE_SUBSCRIPTION_ID=<sub> ./infra/deploy.sh           # whatif
AZURE_SUBSCRIPTION_ID=<sub> ./infra/deploy.sh deploy
```

## CI/CD

- GitHub Actions: `.github/workflows/infra.yml` — `validate` (build/lint + PSRule) su PR,
  `deploy` (what-if → create) su main.
- Azure DevOps: `.azure/pipelines/infra.yml` — speculare.
- Gate **disabled-until-configured**: il deploy è SKIPPED senza la variabile `AZURE_LOCATION`
  (CI verde, zero chiamate Azure). Auth via OIDC (GitHub) / Service Connection (ADO).

## Aggiungere una risorsa

1. Nuovo modulo in `infra/modules/<nome>.bicep` (scope resource group).
2. Invocalo da `main.bicep` con `scope: resourceGroup`.
3. `az bicep build --file infra/main.bicep --stdout` per validare, poi
   `./infra/deploy.sh` (whatif) per vedere il diff prima di applicare.

## Integrazione App Service → Key Vault

Managed identity dell'app passata come `appPrincipalId` (→ Key Vault Secrets User),
app setting `ConnectionStrings__Default = @Microsoft.KeyVault(SecretUri=...)`. Toglie il
secret anche dalle pipeline `cd.yml`/`ci-cd.yml`. Passo successivo, non ancora cablato.
