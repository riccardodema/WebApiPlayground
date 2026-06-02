# GitHub Actions — CI/CD

Pipeline as code per build, test e deploy su Azure. Speculari a quelle Azure DevOps
in `.azure/` (lo stesso progetto implementa entrambe le piattaforme).

## Workflow

| File | Trigger | Scopo |
|---|---|---|
| `build-test.yml` | `workflow_call` (riutilizzabile) | restore + build (solution, incl. DACPAC) + unit & integration test; opzionalmente produce gli artifact `app` e `database` |
| `pr-validation.yml` | `pull_request` → `main` | Gate PR: chiama `build-test`. Da impostare come **required check** |
| `ci-cd.yml` | `push` → `main` / `workflow_dispatch` | `build-test` (con artifact) → job **`deploy`** gated dall'environment `production` |
| `infra.yml` | PR/`push` → `main` su `infra/**` | IaC Bicep: job **`validate`** (build/lint + PSRule) → job **`deploy`** (what-if → `az deployment sub create`) gated dall'environment `production`. Vedi [.claude/context/iac.md](../../.claude/context/iac.md) |

> **Infra deploy disabilitato finché non configuri Azure.** Il job `deploy` di `infra.yml`
> ha `if: vars.AZURE_LOCATION != ''`: senza quella variabile è **saltato** (skipped, non
> failed) → la CI resta verde. Riusa gli stessi secret OIDC (`AZURE_CLIENT_ID`,
> `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`) e l'environment `production` del deploy app.

> **Deploy disabilitato finché non configuri Azure.** Il job `deploy` ha
> `if: vars.AZURE_WEBAPP_NAME != ''`: senza quella variabile viene **saltato**
> (skipped, non fallito) → la CI resta verde e non viene fatta nessuna chiamata ad
> Azure. Appena imposti `AZURE_WEBAPP_NAME` (+ i secret OIDC e l'environment), il
> deploy parte in automatico dopo la CI e si ferma all'**approval gate** prima di
> toccare la produzione (continuous delivery).

Best practice adottate: **workflow riutilizzabile** (DRY, come i template Azure DevOps),
**least-privilege `permissions`**, **concurrency** (cancella run superate), **caching NuGet**,
**OIDC** verso Azure (niente secret long-lived), **approval gate** via GitHub Environments,
schema DB applicato **prima** dell'app + **health check** finale.

## Setup (una tantum)

### 1. Azure OIDC (federated credentials)
Crea una App Registration (o usa un Managed Identity) e una federated credential per il
repo/branch/environment, così GitHub si autentica senza password:

```bash
az ad app create --display-name "github-webapiplayground"
# annota appId (= AZURE_CLIENT_ID) e il tenant/subscription id
# Federated credential per l'environment "production":
#   subject: repo:<owner>/<repo>:environment:production
#   issuer:  https://token.actions.githubusercontent.com
#   audience: api://AzureADTokenExchange
```

Assegna al principal i ruoli RBAC sull'App Service e sul SQL Server (es. `Contributor`
sul resource group per la demo).

### 2. GitHub Environment `production`
Repo → Settings → Environments → **New environment** `production`:
- **Required reviewers** (approval gate) — opzionale ma consigliato per mostrare il flusso.
- **Secrets**: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
  `AZURE_SQL_CONNECTION_STRING`.
- **Variables**: `AZURE_WEBAPP_NAME`.

> La connection string SQL è un **secret**, mai committata. Per Azure SQL si può usare
> anche l'auth AAD al posto di user/password.

### 3. Branch protection su `main`
Repo → Settings → Branches → Add rule:
- Require status checks → seleziona **PR Validation**.
- Require a pull request before merging (min 1 reviewer consigliato).

## Deploy gratis (per portfolio)
Lo stack regge sul free tier: **App Service F1** + **Azure SQL Database (offerta free,
serverless con auto-pause)**. Vedi i caveat (cold start, resume dopo auto-pause, tetto
mensile di compute) prima di lasciarlo live.

## Relazione con Azure DevOps
Le pipeline `.azure/` e questi workflow fanno la stessa cosa con strumenti diversi:
- Schema DB: build del **DACPAC** → publish con **SqlPackage** (`/Action:Publish`).
- App: `dotnet publish` → deploy su App Service.
Scegli la piattaforma in base a dove registri la CI/CD; non vanno eseguite entrambe sullo
stesso ambiente per evitare deploy concorrenti.
