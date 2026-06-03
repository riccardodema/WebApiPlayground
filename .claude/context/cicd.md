# CI/CD — Azure DevOps Pipelines

## Struttura pipeline

```
.azure/
├── pipelines/
│   ├── pr-validation.yml   # Validazione PR: build + unit + integration tests
│   ├── ci.yml              # CI su push a main: build + test + pubblica artifact (app + DACPAC)
│   └── cd.yml              # CD: pubblica DACPAC (schema DB) + deploy su Azure App Service
└── templates/
    ├── steps-restore-build.yml  # Template: UseDotNet, cache NuGet, restore, build
    └── steps-test.yml           # Template: unit test, integration test, coverage
```

### Quando viene eseguita ogni pipeline

| Pipeline | Trigger | Scopo |
|---|---|---|
| `pr-validation.yml` | Apertura/aggiornamento PR verso `main` | Gate obbligatorio prima del merge |
| `ci.yml` | Push/merge su `main` | Produce artifact versionati pronti al deploy |
| `cd.yml` | Manuale o automatico dopo CI | Pubblica il DACPAC (schema DB) e deploya su App Service |
| `infra.yml` | PR/push su `main` che tocca `infra/**` | IaC Bicep: stage Validate (build/lint + PSRule) → stage Deploy (what-if → `az deployment sub create`). Vedi `.claude/context/iac.md` |

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

### Database — DACPAC (SQL project)

Lo schema del DB è versionato come **SQL project** in `database/` (vedi `.claude/context/database.md`).
Il rilascio del DB avviene tramite **DACPAC**, NON tramite EF migrations.

- **CI**: il `dotnet build` della solution (template `steps-restore-build`) compila anche
  `database/WebApiPlayground.Database.sqlproj` producendo il `.dacpac`. Lo step
  _Stage database DACPAC_ copia `.dacpac` + `WebApiPlayground.Database.publish.xml`
  e li pubblica come artifact **`database`**.
- **CD**: scarica l'artifact `database`, installa `Microsoft.SqlPackage` (dotnet tool,
  cross-platform su `ubuntu-latest`) ed esegue `sqlpackage /Action:Publish` con il
  publish profile e `ConnectionStrings__WebConnectionString` come target.
- La pubblicazione del DACPAC va eseguita **prima** del deploy dell'app, così lo schema
  è già aggiornato quando il nuovo codice parte (ordine già rispettato in `cd.yml`).
- Il profilo imposta `BlockOnPossibleDataLoss=True` e `DropObjectsNotInSource=False`:
  un publish che perderebbe dati fallisce invece di procedere silenziosamente.
- Il seed (`Scripts/PostDeployment/Script.PostDeployment.sql`) gira a ogni publish ed è
  idempotente — vedi `database/README.md`.
- Comando equivalente in locale: `./database/deploy.sh` (publish) o `./database/deploy.sh script`
  (genera solo il diff da rivedere).

### Cache NuGet

- Il task `Cache@2` usa il file `packages.lock.json` come chiave. Per abilitare i lock file:
  ```
  dotnet restore --lock-file-path packages.lock.json
  ```
  oppure aggiungere `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` al `.csproj`.
- Se i lock file non esistono, la chiave di cache si basa sull'OS e la cache è meno precisa ma funziona ugualmente.

### Versionamento artifact

- Il `Build.BuildNumber` di Azure DevOps identifica univocamente ogni run CI.
- Gli artifact `drop` (app) e `database` (DACPAC) sono sempre associati al `Build.BuildNumber` della CI che li ha prodotti.
- La pipeline CD specifica la CI sorgente tramite `resources.pipelines` per garantire coerenza tra app e schema DB.

### Percorsi da escludere dai trigger

I path `**/*.md` e `.claude/**` sono esclusi dai trigger CI/PR: modifiche alla documentazione non producono build inutili.

### Health check post-deploy

La pipeline CD esegue un `curl` sull'endpoint **`/health/ready`** dopo il deploy. È il probe di
**readiness** (vedi `.claude/context/health-checks.md`): risponde 200 solo se l'app è in piedi
**e** raggiunge il DB — esattamente la condizione che vogliamo verificare dopo aver pubblicato il
DACPAC e deployato l'app. Se risponde 200 il deploy è riuscito; altrimenti la pipeline fallisce e
il problema è subito visibile.

> Storico: prima si colpiva `/openapi/v1.json`, che però in produzione **non esiste** (OpenAPI è
> mappato solo in Development) — un falso "verde". Sostituito con `/health/ready`.
