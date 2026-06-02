# CI/CD — Azure DevOps Pipelines

## Struttura pipeline

```
.azure/
├── pipelines/
│   ├── pr-validation.yml   # Validazione PR: build + unit + integration tests
│   ├── ci.yml              # CI su push a main: build + test + pubblica artifact
│   └── cd.yml              # CD: applica migrations + deploy su Azure App Service
└── templates/
    ├── steps-restore-build.yml  # Template: UseDotNet, cache NuGet, restore, build
    └── steps-test.yml           # Template: unit test, integration test, coverage
```

### Quando viene eseguita ogni pipeline

| Pipeline | Trigger | Scopo |
|---|---|---|
| `pr-validation.yml` | Apertura/aggiornamento PR verso `main` | Gate obbligatorio prima del merge |
| `ci.yml` | Push/merge su `main` | Produce artifact versionati pronti al deploy |
| `cd.yml` | Manuale o automatico dopo CI | Applica migrations e deploya su App Service |

---

## Configurazione Azure DevOps (setup iniziale)

### 1. Service Connection ARM

In Azure DevOps → Project Settings → Service connections → New service connection → Azure Resource Manager.

Il nome della connessione deve coincidere con la variabile `AzureServiceConnection` nel Variable Group.

### 2. Variable Group `WebApiPlayground-Production`

In Azure DevOps → Pipelines → Library → + Variable group.

| Variabile | Esempio | Note |
|---|---|---|
| `AzureServiceConnection` | `arm-webapi-playground` | Nome service connection ARM |
| `AzureAppServiceName` | `webapi-playground-prod` | Nome App Service su Azure |
| `AzureResourceGroup` | `rg-webapi-playground` | Resource group |
| `ConnectionStrings__WebConnectionString` | `Server=...` | **Segreto**: link a Key Vault |

Spuntare "Link secrets from an Azure key vault" per `ConnectionStrings__WebConnectionString`. Non inserire mai connection string in chiaro nelle variabili.

### 3. Environment `production` con approval gate

In Azure DevOps → Pipelines → Environments → New environment → nome `production`.

Aggiungere un approval gate: Environments → production → Approvals and checks → Approvals. Assegna gli approvatori. La pipeline CD si bloccherà finché non viene approvata manualmente.

### 4. Registrare le pipeline

Per ogni file YAML: Pipelines → New pipeline → Azure Repos Git (o GitHub) → seleziona il file `.azure/pipelines/*.yml`.

Suggerire i nomi:
- `WebApiPlayground - PR Validation`
- `WebApiPlayground - CI`
- `WebApiPlayground - CD`

Il nome `WebApiPlayground - CI` deve coincidere con il campo `source` in `cd.yml` (`resources.pipelines[0].source`).

### 5. Branch policy

In Azure DevOps → Repos → Branches → main → Branch policies:

- Abilita "Build validation" → seleziona la pipeline `WebApiPlayground - PR Validation`
- Spunta "Require a minimum number of reviewers" (consigliato: 1)
- Spunta "Reset votes on new pushes"

---

## Best practice

### Secrets e variabili

- **Mai** inserire connection string o secret direttamente nei file YAML.
- Usare **Variable Groups** collegati ad Azure Key Vault per i segreti di produzione.
- Le variabili non sensibili (`buildConfiguration`, `dotnetVersion`) si dichiarano inline nel YAML.
- In sviluppo locale usare `appsettings.Development.json` (già escluso da `.gitignore`).

### Agent e Docker

- Usare sempre `ubuntu-latest` come `vmImage`.
- Gli integration test usano **Testcontainers.MsSql** che richiede il Docker daemon: `ubuntu-latest` lo include out-of-the-box. Non usare agenti self-hosted senza verificare che Docker sia disponibile.
- Se si usano agenti self-hosted, impostare `TESTCONTAINERS_RYUK_DISABLED: false` come environment variable nel job integration test.

### EF Core Migrations

- In CI si genera un **migrations bundle** self-contained (`efbundle`) invece di eseguire `dotnet ef database update` inline. Il bundle non richiede .NET SDK sull'agent di deploy.
- Il bundle viene pubblicato come artifact separato (`efbundle`) e scaricato dalla pipeline CD.
- Il bundle va eseguito **prima** del deploy dell'applicazione per garantire che lo schema sia aggiornato quando il nuovo codice parte.
- Comando di build bundle (in CI):
  ```
  dotnet ef migrations bundle \
    --project src/WebApiPlayground.Infrastructure \
    --startup-project src/WebApiPlayground.Api \
    --self-contained \
    --output $(Build.ArtifactStagingDirectory)/efbundle
  ```

### Cache NuGet

- Il task `Cache@2` usa il file `packages.lock.json` come chiave. Per abilitare i lock file:
  ```
  dotnet restore --lock-file-path packages.lock.json
  ```
  oppure aggiungere `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` al `.csproj`.
- Se i lock file non esistono, la chiave di cache si basa sull'OS e la cache è meno precisa ma funziona ugualmente.

### Versionamento artifact

- Il `Build.BuildNumber` di Azure DevOps identifica univocamente ogni run CI.
- Gli artifact `drop` e `efbundle` sono sempre associati al `Build.BuildNumber` della CI che li ha prodotti.
- La pipeline CD specifica la CI sorgente tramite `resources.pipelines` per garantire coerenza tra app e migrations bundle.

### Percorsi da escludere dai trigger

I path `**/*.md` e `.claude/**` sono esclusi dai trigger CI/PR: modifiche alla documentazione non producono build inutili.

### Health check post-deploy

La pipeline CD esegue un `curl` sull'endpoint `/openapi/v1.json` dopo il deploy. Se l'endpoint risponde con HTTP 200 il deploy è considerato riuscito; altrimenti la pipeline fallisce e il problema è immediatamente visibile.
