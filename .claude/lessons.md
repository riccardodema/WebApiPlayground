# Lessons Learned — Approcci sbagliati e soluzioni

Ogni entry documenta un approccio che ha causato problemi e la soluzione adottata.
Leggere prima di configurare dipendenze, UI, o infrastruttura.

---

## [L01] Swashbuckle non compatibile con .NET 10

**Approccio errato:** usare `Swashbuckle.AspNetCore` come in .NET 8/9.  
**Errore:** `TypeLoadException` su `GetSwagger` a runtime.  
**Causa:** Swashbuckle 7.x non supporta .NET 10.  
**Soluzione:** sostituire con `Microsoft.AspNetCore.OpenApi` (10.0.0) + `Scalar.AspNetCore` (2.6.0).

```csharp
// Program.cs
builder.Services.AddOpenApi();
app.MapOpenApi();
app.MapScalarApiReference();
```

**URL da usare:** `/scalar/v1` (non `/swagger`).  
**Aggiornare anche:** `launchSettings.json` e `.vscode/launch.json` se referenziano `/swagger`.

---

## [L02] UseHttpsRedirection produce warning rumoroso con il profilo HTTP in development

**Approccio errato:** chiamare `app.UseHttpsRedirection()` incondizionatamente in `Program.cs`.  
**Errore:** `[WRN] Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware: Failed to determine the https port for redirect.` ad ogni request quando si usa il profilo `http` (senza HTTPS configurato).  
**Causa:** Serilog rende visibili i log `Warning` di `Microsoft.AspNetCore` che il logger di default filtrava; `UseHttpsRedirection` non riesce a trovare la porta HTTPS nel profilo HTTP-only.  
**Soluzione:** Applicare il middleware solo fuori da development:

```csharp
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
```

**Nota:** in produzione `UseHttpsRedirection` rimane attivo come atteso.

---

## [L03] Il publish profile del SQL project veniva ignorato da .gitignore → pipeline rotta

**Approccio errato:** dare per scontato che `database/WebApiPlayground.Database.publish.xml` fosse versionato.  
**Errore (CI/CD):** `cp: cannot stat 'database/WebApiPlayground.Database.publish.xml': No such file or directory` nello step _Stage database DACPAC_ (stesso problema su Azure DevOps `ci.yml` e GitHub Actions `build-test.yml`).  
**Causa:** il `.gitignore` (template Visual Studio) ignora `*.[Pp]ublish.xml` (riga ~247, sezione "Publish Web Output", pensata per i profili di deploy web che possono contenere credenziali). Il file non veniva mai committato → assente nel checkout della pipeline. Il `.dacpac` invece c'era perché generato dal build sull'agent.  
**Soluzione:** il nostro profilo contiene **solo opzioni di deploy, nessuna credenziale**, quindi va versionato. Aggiunta un'eccezione in `.gitignore` (dopo la regola generale, così l'ultima regola vince):

```gitignore
!database/WebApiPlayground.Database.publish.xml
```

Poi committare sia `.gitignore` sia il `.publish.xml`. Verifica: `git check-ignore <file>` deve uscire con codice 1 e il file deve comparire in `git status` come `??`.

**Nota:** i secret (connection string) NON stanno mai nel profilo — sono passati a SqlPackage via `/TargetConnectionString` dalla pipeline (Variable Group / GitHub Environment).

---

## [L04] `paths-ignore` su un workflow che è required status check → PR non mergeabili

**Approccio errato:** mettere `paths-ignore: ['**/*.md', ...]` su `pr-validation.yml` e poi renderlo *required status check* nella branch protection di `main`.  
**Errore:** una PR che tocca solo path ignorati (es. solo `.md`) **non fa partire** il workflow → il check `validate / build-test` resta "Expected — Waiting for status to be reported" → la PR **non è mai mergeabile** (deadlock con la branch protection). Sintomo: `gh pr checks` dice `no checks reported on the branch`.  
**Causa:** GitHub richiede che il check riportato esista; con `paths-ignore` il workflow è skippato e il check non viene mai creato.  
**Soluzione:** togliere `paths-ignore` dal workflow usato come check obbligatorio (deve girare su **ogni** PR). In alternativa, pattern "ghost check" con un job fallback che riporta successo sui path ignorati. Il `paths-ignore` resta ok su workflow NON obbligatori (es. `ci-cd.yml` sul push a main).

---

## [L05] Bicep / Key Vault — pitfall ricorrenti su IaC

**Approccio errato:** scrivere il Bicep del Key Vault "a senso" senza considerare vincoli e linter.
**Errori e cause:**
- **Purge protection non disabilitabile.** Impostare `enablePurgeProtection: false` su un vault dove era già attiva fa fallire il deploy (ARM rifiuta il downgrade). → Usare `enablePurgeProtection: enable ? true : null` (mai `false` esplicito).
- **Nome KV global-unique ≤24 char.** Il nome è globale su tutto Azure, 3-24 caratteri, alfanumerico + `-`. Hardcodarlo causa collisioni. → `take('kv-${workload}-${env}-${take(uniqueString(subscription().id, resourceGroup().id), 6)}', 24)` (deterministico = idempotente).
- **Linter `use-recent-api-versions`.** API version oltre ~730 giorni → warning. Usare versioni recenti (es. `Microsoft.KeyVault/vaults@2024-11-01`).
- **`az bicep build` vs binario standalone.** Il task CI usa `az bicep build --file <f>`; il binario `bicep` standalone vuole il path **posizionale** (`bicep build <f>`). Non sono intercambiabili sui flag.
- **PSRule richiede la Bicep CLI** per espandere `.bicep`/`.bicepparam` in ARM (`AZURE_BICEP_FILE_EXPANSION: true`). Senza Bicep installata, le regole non vedono nulla.
- **what-if richiede ruolo Reader** (oltre a quello di scrittura per il deploy) sulla subscription: serve a leggere lo stato corrente per calcolare il diff.
- **Skip condizionale in xUnit 2.x.** `Assert.Skip`/`Assert.SkipUnless` esistono solo in xUnit **v3**. In v2 (qui 2.9.3) usare il pacchetto `Xunit.SkippableFact` con `[SkippableFact]` + `Skip.IfNot(cond, "...")`. Usato in `tests/WebApiPlayground.IacTests` per skippare se la Bicep CLI è assente invece di fallire.

**Soluzione:** vedi `infra/` e `tests/WebApiPlayground.IacTests/` — scelte già applicate. Anteprima sempre con `./infra/deploy.sh` (default `whatif`) prima di `deploy`.

---

## [L06] Entra ID / autorizzazione — pitfall su Microsoft.Identity.Web e OpenAPI in .NET 10

**Contesto:** proteggere gli endpoint con Entra ID (JWT) + policy scope-or-app-permission. Dettagli in `.claude/context/auth.md`.
**Errori e cause:**
- **Ordine middleware.** `app.UseAuthentication()` deve stare **prima** di `app.UseAuthorization()`. Invertiti o senza `UseAuthentication`, ogni richiesta è anonima → 401 anche con token valido. (Il `Program.cs` originale aveva solo `UseAuthorization`.)
- **Claim `scp` vs `roles`.** Lo scope delegato arriva nel claim `scp` (valori separati da spazio in un singolo claim); l'app permission (macchina→macchina) nel claim `roles` (una entry per ruolo). `RequireScopeOrAppPermission(scopes, appPermissions)` di Microsoft.Identity.Web accetta l'uno **o** l'altro → copre sia utente→API sia daemon. Verificato che un permesso di sola lettura su un endpoint di scrittura → 403.
- **OpenAPI 2.0 ha cambiato namespace in .NET 10.** `Microsoft.AspNetCore.OpenApi` 10.0 porta `Microsoft.OpenApi` **2.0**: i tipi stanno in `Microsoft.OpenApi` (non più `Microsoft.OpenApi.Models`), `SecuritySchemes` è `IDictionary<string, IOpenApiSecurityScheme>`, e i reference si fanno con `new OpenApiSecuritySchemeReference(id, document, null)` (non più `OpenApiReference`). Un document transformer scritto per OpenApi 1.x non compila.
- **Test senza tenant reale.** Non serve Entra per i test: si sostituisce lo schema JWT con un `AuthenticationHandler` fittizio impostato come default in `WebApplicationFactory.ConfigureWebHost` (la config registrata dopo quella dell'app vince sul `DefaultScheme`). Senza header → `AuthenticateResult.NoResult()` per testare il 401.

**Soluzione:** vedi `Api/Extensions/AuthenticationExtensions.cs`, `Api/OpenApi/BearerSecuritySchemeTransformer.cs`, `tests/WebApiPlayground.IntegrationTests/Infrastructure/TestAuthHandler.cs`.

---

## [L07] Paginazione offset: OFFSET/FETCH senza ORDER BY deterministico → pagine non ripetibili

**Approccio errato:** ordinare la query paginata solo per la colonna richiesta (es. `OrderBy(b => b.Title)`) e applicare `Skip/Take`.
**Errore:** con valori non univoci (titoli o autori omonimi) l'ordine tra righe "pari" non è garantito; SQL Server può restituire **la stessa riga su pagine diverse** o saltarne una. Sintomo: elementi che "ballano" tra le pagine, test di sorting flaky.
**Causa:** `OFFSET ... FETCH` richiede un ordinamento **totale** (deterministico) per essere ripetibile; un `ORDER BY` su colonna non univoca è solo parziale.
**Soluzione:** aggiungere sempre un **tiebreaker sulla PK**: `.OrderBy(b => b.Title).ThenBy(b => b.Id)` (idem nei rami `Descending`). Vedi `BookRepository.GetPagedAsync` e la sez. *Paginazione* in `.claude/context/conventions.md`.

**Note aggiuntive:**
- Validare i parametri con `[Range]` su `BooksQueryParameters` + `[ApiController]` → 400 ProblemDetails automatico; non serve codice manuale.
- La **whitelist** dei campi sort va nel service (mai passare la stringa utente grezza all'`OrderBy`): valori fuori whitelist → fallback a `id`, non 400.
- `Skip/Take` + `CountAsync` su `IQueryable` traducono in `OFFSET/FETCH` + `COUNT(*)`: ok su Azure SQL Edge (DSP `Sql150`). Non materializzare con `ToList()` prima di paginare.

---

## [L08] WebApplicationFactory gira in Development → il Detail dev-only "trapela" nei test; endpoint di test via ApplicationPart

**Contesto:** testare `GlobalExceptionHandler` (eccezione non gestita → ProblemDetails 500). Dettagli in `.claude/context/error-handling.md`.
**Errori e cause:**
- **Ambiente di test = Development.** `WebApplicationFactory<Program>` avvia l'app in ambiente **Development** di default (lo conferma anche la guard `if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();` — in non-Development il redirect HTTPS romperebbe i test HTTP via TestServer con 307). Conseguenza: il `Detail` del ProblemDetails — che includiamo **solo in Development** per non fare info-leak in prod — *è presente* nei test. Un assert tipo `DoesNotContain(messaggioEccezione)` **fallisce**. → Non testare il comportamento prod-only sotto WebApplicationFactory senza forzare l'ambiente; testare invece ciò che è env-agnostico (status 500, `application/problem+json`, `correlationId` nel body = header).
- **Esercitare un handler 500 senza endpoint fittizi in produzione.** Serve un endpoint che lancia, ma non deve esistere nell'app reale. → Mettere un `ThrowingTestController` nell'assembly dei test e iniettarlo nella pipeline reale con `services.AddControllers().AddApplicationPart(typeof(PlaygroundApiFactory).Assembly)` dentro `ConfigureWebHost`. L'endpoint esiste solo quando gira la factory di test.
- **correlationId nel body d'errore.** L'handler gira fuori dallo scope Serilog `LogContext`, quindi non "vede" la property `CorrelationId`. → Il `CorrelationIdMiddleware` salva l'id anche in `HttpContext.Items[ItemKey]`; l'enrichment `CustomizeProblemDetails` lo rilegge da lì. `UseExceptionHandler` deve stare **dopo** il middleware del correlation id.

**Soluzione:** vedi `Api/ErrorHandling/GlobalExceptionHandler.cs`, `Api/Extensions/ErrorHandlingExtensions.cs`, `tests/WebApiPlayground.IntegrationTests/Infrastructure/ThrowingTestController.cs` e `.../ErrorHandling/GlobalExceptionHandlerTests.cs`.

---

## [L09] Health-check post-deploy su un endpoint disponibile solo in Development = falso verde

**Approccio errato:** usare `/openapi/v1.json` come endpoint di health-check post-deploy nelle pipeline CD.
**Errore:** in produzione l'endpoint **non esiste** (in `Program.cs` `MapOpenApi`/`MapScalarApiReference` sono dentro `if (app.Environment.IsDevelopment())`), quindi il `curl` o falliva o — peggio — dava un verde non rappresentativo dello stato reale dell'app.
**Causa:** confondere "un endpoint risponde" con "l'app è pronta a servire". OpenAPI è un dettaglio di sviluppo, non un segnale di salute; e qui era pure spento in prod.
**Soluzione:** endpoint dedicati liveness/readiness sempre attivi. Il CD colpisce `/health/ready` (readiness): 200 solo se l'app è su **e** raggiunge il DB — la condizione giusta dopo publish del DACPAC + deploy. Vedi `.claude/context/health-checks.md` e `.claude/context/cicd.md`.

**Note aggiuntive:**
- **Liveness ≠ readiness.** Mai mettere check di dipendenze (DB) nel liveness: un DB giù farebbe **riavviare** l'app invece di toglierla solo dal routing. Liveness = `Predicate = _ => false`; readiness = `Predicate = c => c.Tags.Contains("ready")`.
- I probe devono essere **anonimi** (l'orchestratore non ha token) e **mappati in ogni ambiente** (fuori dal blocco `IsDevelopment`).

---

<!-- Template per nuove entry:
## [L0N] Titolo breve

**Approccio errato:** ...  
**Errore:** ...  
**Causa:** ...  
**Soluzione:** ...
-->
