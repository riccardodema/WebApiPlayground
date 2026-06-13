# Test strategy & test quality — la suite che dimostra se stessa

**Scopo:** non solo *avere* test (piramide su 5 progetti) ma **dimostrare che funzionano**:
mutation testing, coverage gate ratchet, test di parità DACPAC↔EF e pipeline JWT reale.
Movente: [L25] — un e2e passava sul trasporto sbagliato. Pitfalls: [L27] [L28].

## Mappa

| Cosa | Dove |
|---|---|
| Tool versionati (reportgenerator, stryker) | `.config/dotnet-tools.json` (`dotnet tool restore`) |
| Scoping coverage (solo assembly src) | `tests/coverlet.runsettings` (passato con `--settings`) |
| Soglie gate coverage (RATCHET) | `tests/coverage-thresholds.json` |
| Config Stryker (threshold break = ratchet; NIENTE chiavi commento "//": le rifiuta) | `stryker-config.json` (root) |
| Orchestratore mutation per-progetto (CI e locale) | `tests/run-mutation.sh` |
| CI: collect+merge+gate coverage, mutation incrementale | `.github/workflows/build-test.yml` |
| CI: badge self-hosted (branch `badges`) | job `badges` in `ci-cd.yml` |
| Mutation FULL (solo manuale, MAI schedulata) | `.github/workflows/mutation-full.yml` |
| Parità DACPAC↔EF | `tests/.../IntegrationTests/Database/` (`DacpacDeployedApiFactory` + `DacpacSchemaParityTests`) |
| JWT reale (authority OIDC finta) | `tests/.../IntegrationTests/Auth/` (`FakeOidcAuthority` + `RealJwtAuthTests`) |

## Regole e razionali

- **Coverage = ratchet, non soglia fissa.** Gate su line+branch combinata (unit+integration,
  merged con ReportGenerator) contro `tests/coverage-thresholds.json`: i valori partono dalla
  baseline misurata e possono solo SALIRE. Abbassarli richiede una modifica esplicita e motivata
  in PR. La coverage è scopata ai soli assembly `src/*` (runsettings); Domain non compare nei
  report: è fatto di soli POCO auto-property, zero sequence points — è normale, non un buco.
- **Mutation testing è il gate di QUALITÀ** (la coverage dice solo che il codice è eseguito, non
  che le asserzioni mordano). Stryker gira: (a) **incrementale su ogni PR** (`--since:origin/<base>`, solo file cambiati,
  **INFORMATIVA**: `--break-at 0`, punteggio nel job summary — una soglia fissa bloccherebbe PR
  legittime sul codice coperto solo da integration test); (b) **full SOLO a richiesta** via `workflow_dispatch`
  (`mutation-full.yml`) con la soglia `break` ratchet in `stryker-config.json`. **Niente run
  schedulate**: scelta deliberata dell'utente (un cron dimenticato gira per sempre).
  **SEMPRE via `tests/run-mutation.sh`** (una run per layer, test bed = soli unit test): il
  solution mode arruolerebbe TUTTI i test project — integration coi container, ore di run e
  flakiness — vedi [L29]. Il codice coperto solo da integration appare come `NoCoverage`.
  **Score (giu 2026):** Application 84.8%, Infrastructure 82.6%, Api 80.5% → combinato **81.7%**
  (564/690), partendo da 25.7% — vedi [L30] per la strategia di rinforzo (comportamento, non
  implementazione). `break` ratchet = **78** (sotto il minimo per-progetto, si applica PER RUN):
  alzarlo man mano che cresce, mai abbassarlo.
- **Cosa è ESCLUSO dalla mutazione (e perché), in `stryker-config.json` `mutate`:** composition
  root / wiring dichiarativo già coperto da integration/IaC/arch test e non unit-testabile senza
  accoppiarsi all'implementazione del bootstrap — `Program.cs`, `PlaygroundDbContext`
  (OnModelCreating), `DependencyInjection`, le `*Registration`/`*Extensions` di registrazione, il
  consumer/publisher Service Bus e l'`OutboxDispatcher` (trasporto, esercitato dall'emulatore negli
  integration test), gli endpoint health, e `ApiVersioningExtensions`/`*.generated.cs`
  (interceptor del source generator OpenAPI — [L29]). Escludere ALTRO richiede la stessa
  giustificazione: "wiring, coperto altrove", mai "difficile da testare".
- **Badge self-hosted, niente Codecov**: job `badges` (ci-cd) e mutation-full scrivono JSON
  shields-endpoint sul branch `badges` (orfano, fuori da main → niente trigger/rumore); il README
  li consuma via `img.shields.io/endpoint`. Nessun report a servizi terzi, nessun token extra.
- **Parità DACPAC↔EF**: il resto della suite usa `EnsureCreated` (schema dal modello EF) ma in
  compose/produzione lo schema lo pubblica il DACPAC → drift invisibile ai test [L27]. La suite
  dedicata deploya il pacchetto vero (DacFx programmatico, niente sqlpackage) e: confronto
  strutturale colonna-per-colonna (tipo+nullabilità, con normalizzazioni timestamp→rowversion e
  scale di default), SELECT reale su OGNI entità mappata, write-path completo (IDENTITY, FK,
  rowversion/If-Match, outbox→snapshot) e paginazione sul seed dei 100 libri.
- **JWT reale**: `TestAuthHandler` resta il default (veloce), ma `RealJwtAuthTests` esercita la
  pipeline `AddMicrosoftIdentityWebApi` VERA contro `FakeOidcAuthority` (Kestrel in-proc, discovery
  + JWKS + RSA per-run). Matrice: firma estranea/garbage/scaduto/nbf futuro/audience/issuer → 401;
  scope insufficiente → 403; scp delegato e roles app-permission → 200. Adattamenti dichiarati e
  minimi: `RequireHttpsMetadata=false` (loopback http) e issuer col confronto standard al posto di
  `AadIssuerValidator` (vuole il metadata Entra reale). Pitfall di wiring in [L28].

## Comandi locali

```bash
dotnet tool restore

# Coverage con report HTML locale
dotnet test tests/WebApiPlayground.Tests/... --settings tests/coverlet.runsettings --collect "XPlat Code Coverage" --results-directory /tmp/cov
dotnet test tests/WebApiPlayground.IntegrationTests/... --settings tests/coverlet.runsettings --collect "XPlat Code Coverage" --results-directory /tmp/cov
dotnet tool run reportgenerator -reports:"/tmp/cov/*/coverage.cobertura.xml" -targetdir:/tmp/cov/report -reporttypes:"Html;TextSummary"

# Mutation testing (full locale; incrementale: ./tests/run-mutation.sh --since:main --break-at 0)
./tests/run-mutation.sh
```

## Quando si aggiunge codice nuovo

1. Test first/insieme: unit per la logica, integration se attraversa I/O reale.
2. Se introduce un ramo config-gated → **probe strutturale** che il ramo sia DAVVERO attivo nel
   test e2e ([L25]: mai fidarsi del verde senza probe).
3. Se cambia lo schema → aggiorna `database/` (DACPAC) E il modello EF: la parità rompe se divergono.
4. La PR deve passare il gate coverage ratchet; la mutation incrementale è informativa ma va
   LETTA: mutanti sopravvissuti nel codice nuovo = asserzioni deboli, rinforzale (non abbassare
   le soglie). Il gate duro di mutazione è il break ratchet della full manuale.
