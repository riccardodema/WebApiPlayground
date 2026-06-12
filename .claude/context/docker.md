# Dockerfile + docker-compose — containerizzazione dell'API

**Scopo didattico:** trasformare l'API in un **artefatto di runtime portabile e riproducibile**
(immagine container, `Dockerfile`) e dare uno **stack di sviluppo completo in un comando**
(`docker compose up`). Fino a qui Docker serviva *solo ai test* (Testcontainers); qui si
containerizza **l'applicazione vera** e le sue dipendenze di runtime.

## Testcontainers vs Dockerfile vs docker-compose (la differenza chiave)

Sono tre usi di Docker **complementari**, non alternativi:

| | Cosa containerizza | Ciclo di vita | A cosa serve |
|---|---|---|---|
| **Testcontainers** (`tests/…IntegrationTests`) | solo **la dipendenza** (SQL Server) | effimero, creato/distrutto **dal processo di test** | integration test isolati e deterministici. L'app gira *in-process* (`WebApplicationFactory`), non in container. |
| **Dockerfile** (`/Dockerfile`) | **l'app** | l'immagine è l'artefatto di build/deploy | far girare *la stessa* immagine ovunque (dev → CI → prod). |
| **docker-compose** (`/docker-compose.yml`) | app + dipendenze reali (DB, opz. Redis/OTLP) | ambiente locale, `up`/`down` | eseguire/provare lo **stack reale** in locale; documentare la topologia di runtime as-code. |

**Il Dockerfile NON cambia come girano i test.** Testcontainers resta: è per i test. compose è
per *eseguire l'app*.

### Cosa dà in più rispetto a Testcontainers
1. **Artefatto di runtime riproducibile** — addio "works on my machine": stessa immagine ovunque, parità con la produzione.
2. **Onboarding in un comando** — `docker compose up` → API + DB + schema (DACPAC) + seed pronti. Prima serviva installare SQL Server e lanciare `deploy.sh` a mano.
3. **Stack reale, non lo stub dei test** — gira l'app vera (no `WebApplicationFactory`) contro SQL/Redis/OTLP reali, incluse le parti che i test stubbano (auth, Open Library); si naviga Scalar contro lo stack.
4. **Target di deploy** — l'immagine va in un registry e gira su App Service for Containers / Kubernetes / Container Apps (passo successivo: push su GHCR/ACR).
5. **Runtime ridotto e hardened** — base **chiseled non-root** (no shell, superficie d'attacco minima, pull veloci).

## Dockerfile (immagine API): multi-stage + chiseled non-root

[`Dockerfile`](../../Dockerfile) — due stage:

- **`build`** su `mcr.microsoft.com/dotnet/sdk:10.0`: copia **prima i soli `.csproj`** e fa `restore`
  (il layer resta in cache finché non cambia un progetto → build incrementali veloci), poi copia `src/`
  e `dotnet publish` framework-dependent.
- **`final`** su `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`: immagine **chiseled**
  (distroless-style: niente shell né package manager), gira come utente **non-root `app`** (UID 64198,
  `USER $APP_UID`), porta **8080** (`ASPNETCORE_HTTP_PORTS`, non privilegiata). La variante **`-extra`**
  (chiseled + **ICU** + tzdata) è obbligatoria qui: `Microsoft.Data.SqlClient` (EF Core) **non** supporta la
  Globalization Invariant Mode → con la chiseled "liscia" l'app parte ma ogni query DB fallisce. Vedi [L23].

Niente `HEALTHCHECK` nel Dockerfile: l'immagine chiseled **non ha shell/curl**, quindi le probe sono
**HTTP esterne** (compose/orchestratore). Vedi [L23].

Il [`.dockerignore`](../../.dockerignore) tiene il context piccolo e — soprattutto — **non fa finire
`appsettings.Development.json` nell'immagine** (la connection string locale non si imbarca: in container
arriva da env `ConnectionStrings__Default`).

## Servizio migrations (schema DACPAC, source of truth)

L'app si aspetta le tabelle già presenti. Lo schema **non** lo crea l'app: lo pubblica un job one-shot,
fedele a "DACPAC = source of truth" (come `deploy.sh` in locale e in CI/CD).

[`database/Dockerfile`](../../database/Dockerfile): SDK + `microsoft.sqlpackage` come global tool,
pre-builda il DACPAC e ha per ENTRYPOINT **lo stesso [`deploy.sh`](../../database/deploy.sh)** (build +
`sqlpackage /Action:Publish`, seed incluso). Legge `DB_CONNECTION` dall'ambiente. Esce a `0` → l'API parte
con `depends_on … service_completed_successfully`.

## docker-compose (stack locale)

[`docker-compose.yml`](../../docker-compose.yml) — quattro servizi:

- **`db`** (`mssql/server:2022-latest`): `ACCEPT_EULA`, `MSSQL_SA_PASSWORD` da `.env`, volume per la
  persistenza, **healthcheck** via `sqlcmd … SELECT 1`. `platform: linux/amd64` (vedi note Apple Silicon).
- **`servicebus`** (`azure-messaging/servicebus-emulator`): **emulatore ufficiale** di Azure Service Bus →
  in compose l'outbox gira sul **broker reale** (publisher → coda → consumer), non in-process. Coda dichiarata in
  [`docker/servicebus-emulator/Config.json`](../../docker/servicebus-emulator/Config.json). Riusa il container
  `db` come backend SQL (`SQL_SERVER=db`, l'emulatore lo richiede) → niente terzo container. `platform:
  linux/amd64`. L'app vi punta via `ServiceBus__ConnectionString` (host = nome servizio, `UseDevelopmentEmulator=true`).
  Vedi `outbox.md` (sez. PR-2) e [L24].
- **`keyvault`** (+ job one-shot **`keyvault-certs`** e **`keyvault-seed`**): **emulatore community di Azure
  Key Vault** (immagine **pinnata** `jamesgoulddev/azure-keyvault-emulator:3.1.0`; tag/platform overridabili da
  `.env` per arm64 nativo) = la **fonte dei segreti** dello stack. `keyvault-certs` genera il cert TLS
  self-signed nel volume (l'SDK Azure pretende https; PFX password `emulator` = contratto dell'immagine),
  `keyvault-seed` fa il PUT REST dei secret (connection string di DB ed emulatore ASB — **non più nell'env
  dell'api**), l'api li carica all'avvio col config provider (`KeyVault__Uri=https://keyvault:4997`,
  `KeyVault__Credential=Emulator`, solo Development). Vedi `keyvault.md`, `docs/keyvault.md` e [L26].
- **`db-migrations`**: build di `database/`, `DB_CONNECTION` verso `db`, parte **dopo** `db` *healthy*
  (sqlpackage non parla col vault: la sua connection string resta env, parametrizzata da `.env`).
- **`api`**: build del `Dockerfile` di root, `8080:8080`, attende `db` *healthy*, `db-migrations` e
  `keyvault-seed` *completed_successfully* (i segreti devono essere nel vault PRIMA dello startup) e
  `servicebus` *started* → all'avvio dell'API schema, segreti e broker ci sono (il consumer riprova comunque
  lo start finché l'emulatore non è pronto).

```bash
cp .env.example .env            # imposta MSSQL_SA_PASSWORD (password "forte")
docker compose up --build
# API + Scalar: http://localhost:8080/scalar/v1   (gira in Development)
```

La SA password vive in **`.env`** (gitignored); il repo versiona solo [`.env.example`](../../.env.example).

### Dipendenze opzionali (off di default) — file di override

Non sono nel compose base: si attivano aggiungendo un file di override (più robusto dei `profiles`
perché deve cablare **anche l'env dell'`api`**, non solo accendere un servizio):

```bash
docker compose -f docker-compose.yml -f docker-compose.redis.yml  up   # Redis: cache L2 + backplane
docker compose -f docker-compose.yml -f docker-compose.aspire.yml up   # Aspire Dashboard: telemetria OTLP (UI :18888)
```

- [`docker-compose.redis.yml`](../../docker-compose.redis.yml): servizio `redis` + `Cache__Redis__ConnectionString` → attiva L2/backplane (config-gated, vedi `caching.md`).
- [`docker-compose.aspire.yml`](../../docker-compose.aspire.yml): `aspire-dashboard` + `OpenTelemetry__OtlpEndpoint` → export OTLP (config-gated, vedi `opentelemetry.md`).

## Ambiente e configurazione: Development (locale) vs Production (immagine)

L'**immagine** di default è **Production** (12-factor). compose la fa girare in **Development**
(`ASPNETCORE_ENVIRONMENT=Development`) di proposito: così in locale hai **Scalar UI**, **auth in BYPASS**
(niente Entra) e **niente HTTPS redirect** — l'onboarding è davvero "un comando".

**In Production l'app fa fail-fast esplicito** se manca configurazione obbligatoria
([`StartupConfigurationValidator`](../../src/WebApiPlayground.Api/Configuration/StartupConfigurationValidator.cs),
chiamato in testa a `Program.cs`): rifiuta l'avvio elencando **tutte** le chiavi mancanti
(`ConnectionStrings:Default`, `AzureAd:ClientId/TenantId/Audience`) con anche la forma env var
(`ConnectionStrings__Default`, `AzureAd__ClientId`, …). Per girare l'immagine in Production:

```bash
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__Default="Server=…;Database=…;User ID=…;Password=…;" \
  -e AzureAd__ClientId="…" -e AzureAd__TenantId="…" -e AzureAd__Audience="api://…" \
  webapiplayground:latest
```

Vedi `auth.md` (gate `!IsDevelopment` condiviso col bypass) e [L23].

## Note pratiche

- **Apple Silicon (arm64).** L'immagine SQL Server è **solo amd64** → `platform: linux/amd64` la fa girare
  in **emulazione** (più lenta, ma funziona). Stesso vincolo già presente con Testcontainers. Vedi [L23].
- **Chiseled = no shell.** Non `docker exec … bash` né HEALTHCHECK con curl: le probe sono HTTP, dall'esterno.
- **Porta 1433 già occupata.** Se hai un SQL Server / Azure SQL Edge locale, lo `up` dà `port is already
  allocated`. L'API parla al DB via rete interna (`db:1433`), quindi la pubblicazione host serve solo a
  client esterni: imposta `SQL_HOST_PORT` in `.env` (es. `14330`) per pubblicarlo altrove. Vedi [L23].

## Test

`tests/WebApiPlayground.DockerTests` — stesso spirito di `IacTests` (assertano la *posture* leggendo gli
artefatti as-code):

- **Contract test statici** ([`DockerArtifactsContractTests`](../../tests/WebApiPlayground.DockerTests/DockerArtifactsContractTests.cs)),
  veloci, **senza Docker**: il Dockerfile è multi-stage + chiseled + non-root + porta 8080; `.dockerignore`
  esclude `appsettings.Development.json`/bin/obj; il compose ha db con healthcheck e `platform: linux/amd64`,
  l'`api` attende `service_healthy` + `service_completed_successfully`, **nessun segreto in chiaro**
  (`${MSSQL_SA_PASSWORD}`); il `database/Dockerfile` installa sqlpackage e riusa `deploy.sh`; gli override
  cablano L2/OTLP.
- **Smoke test live** ([`ContainerSmokeTests`](../../tests/WebApiPlayground.DockerTests/ContainerSmokeTests.cs)),
  `[SkippableFact]` (salta senza Docker, come IacTests senza Bicep CLI): builda l'immagine dal `Dockerfile`,
  avvia il container in Development e verifica `GET /health/live == 200`. È la **liveness** (il processo parte
  anche senza DB: l'`OutboxDispatcher` isola gli errori di batch). Vale anche da **validazione del `docker build`**.

In CI gira su entrambe le piattaforme ([build-test.yml](../../.github/workflows/build-test.yml),
[steps-test.yml](../../.azure/templates/steps-test.yml)), dove Docker è disponibile come per i Testcontainers.

## File chiave

- [`Dockerfile`](../../Dockerfile) · [`.dockerignore`](../../.dockerignore) — immagine API.
- [`database/Dockerfile`](../../database/Dockerfile) · [`database/.dockerignore`](../../database/.dockerignore) — servizio migrations (riusa `deploy.sh`).
- [`docker-compose.yml`](../../docker-compose.yml) + [`docker-compose.redis.yml`](../../docker-compose.redis.yml) + [`docker-compose.aspire.yml`](../../docker-compose.aspire.yml) + [`.env.example`](../../.env.example) — stack locale.
- [`StartupConfigurationValidator`](../../src/WebApiPlayground.Api/Configuration/StartupConfigurationValidator.cs) — fail-fast config in non-Development.
- [`tests/WebApiPlayground.DockerTests`](../../tests/WebApiPlayground.DockerTests/) — contract + smoke test.
- Pitfall: `.claude/lessons.md` [L23].
