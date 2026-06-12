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

## [L10] `IProblemDetailsService` serializza sul tipo statico `ProblemDetails` → la mappa `errors` sparisce

**Approccio errato:** produrre la risposta 400 di validazione costruendo un `ValidationProblemDetails`
e scrivendolo via `IProblemDetailsService.TryWriteAsync` (per riusare `CustomizeProblemDetails` e
ottenere `application/problem+json` "gratis").
**Errore:** il body usciva come ProblemDetails "base" — `type`/`title`/`status`/`detail` presenti ma
**senza la proprietà `errors`** (i campi invalidi e i relativi messaggi). Un client non sa più *cosa*
correggere.
**Causa:** `DefaultProblemDetailsWriter` serializza `context.ProblemDetails` sul **tipo statico**
`ProblemDetails`, non sul runtime type: i membri di `ValidationProblemDetails` (tra cui `Errors`)
vengono scartati dalla serializzazione polimorfica.
**Soluzione:** per la validazione serializzare **sul tipo concreto** con
`Response.WriteAsJsonAsync(pd, pd.GetType(), contentType: "application/problem+json")`, arricchendo a
mano con il `ProblemDetailsEnricher` condiviso. `IProblemDetailsService` resta giusto per il
`GlobalExceptionHandler` (che scrive un `ProblemDetails` base, senza `errors`). Vedi
`Api/Validation/ValidationProblemDetailsFactory.cs` e `.claude/context/validation.md`.

**Nota aggiuntiva:** un `ObjectResult`/`BadRequestObjectResult` con `ContentTypes` impostato a
`application/problem+json` **non** garantisce quel content-type: la negoziazione MVC può comunque
emettere `application/json`. Scrivere direttamente sulla `Response` è deterministico.

---

## [L11] Cache server-side + test che fanno seed diretto sul DB → letture stale/flaky

**Contesto:** introdotto il caching server-side con un decoratore `CachingBooksService` su
`HybridCache` (FusionCache). Dettagli in `.claude/context/caching.md`.
**Approccio errato:** lasciare i test d'integrazione esistenti invariati. Quelli fanno **seed
diretto sul DB** via `DbContext` (per arrangiare lo stato) e poi chiamano gli endpoint GET.
**Errore:** GET che restituiscono dati **vecchi** → assert falliti in modo non deterministico
(flaky) a seconda dell'ordine dei test.
**Causa:** il seed diretto **bypassa l'API** e quindi il decoratore di caching, che invalida solo
sulle scritture *attraverso il service*. La `WebApplicationFactory` è condivisa nella collection,
quindi la cache **L1 in memoria** sopravvive tra un test e l'altro: una pagina cache-ata in un test
viene restituita stale in quello successivo, nonostante il `DELETE FROM Books` del reset.
**Soluzione:** nel reset condiviso (`PlaygroundApiFactory.ResetDatabaseAsync`) svuotare anche la
cache: `cache.RemoveByTagAsync(BookCacheKeys.Books)`. Il reset del DB e quello della cache vanno
sempre insieme.

**Note aggiuntive:**
- **FusionCache target net8.0 su net10**: il pacchetto `ZiggyCreatures.FusionCache` 2.6.0 dichiara
  `net8.0`/`netstandard2.0`; gira senza problemi su net10 (compatibilità in avanti), non serve un
  target dedicato.
- **Conflitto di versione con HybridCache**: `Microsoft.Extensions.Caching.Hybrid` **10.6.0** tira
  dipendenze transitive `Microsoft.Extensions.*` **10.0.8**, in conflitto col baseline **10.0.0**
  pinnato nei progetti → `NU1605` (downgrade come errore). Allineare l'Hybrid a **10.0.0** (i tag e
  `RemoveByTagAsync` ci sono già da .NET 9), non bumpare tutto il resto.
- **`ConfigurationBinder.Get<T>()`** richiede il pacchetto `Microsoft.Extensions.Configuration.Binder`:
  `GetConnectionString`/indexer funzionano senza, ma il binding tipizzato di `CacheSettings` no.

---

## [L12] `AddMicrosoftIdentityWebApi` con `AzureAd` vuoto → IDW10106 500 su OGNI richiesta (anche Scalar)

**Contesto:** Entra ID configurato ma con sezione `AzureAd` ancora vuota (app registration non creata).
Dettagli in `.claude/context/auth.md`.
**Approccio errato:** registrare incondizionatamente
`AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddMicrosoftIdentityWebApi(GetSection("AzureAd"))`.
**Errore:** avviando in locale, **ogni** richiesta — inclusa `/scalar/v1` — risponde **500** con
`IDW10106: The 'ClientId' option must be provided.`; la UI di Scalar non si apre nemmeno.
**Causa:** Microsoft.Identity.Web valida (lazy) che `ClientId` sia presente la prima volta che le
`JwtBearerOptions` vengono risolte. Avendo impostato Bearer come **schema di default**,
`app.UseAuthentication()` lo risolve per *ogni* richiesta → la validazione scatta sempre → 500
ovunque, non solo sugli endpoint protetti.
**Soluzione:** gate *disabled-until-configured* in `AddApiAuthentication` (vedi anche il param
`IHostEnvironment`):
- `AzureAd:ClientId` presente → Entra reale (come prima);
- assente + **Development** → `DevelopmentAuthHandler` (autentica `dev-user` con scope pieni, così
  Scalar è usabile senza tenant/token);
- assente + non-Development → eccezione allo startup (mai anonimo silenzioso in prod).

**Nota aggiuntiva (verifica in locale):** `dotnet run` applica il profilo di `launchSettings.json`,
il cui `applicationUrl` **vince** su `ASPNETCORE_URLS`. Per avviare una seconda istanza su un'altra
porta (es. per testare senza toccare quella già attiva sulla 5242) usare
`dotnet run --no-launch-profile --urls http://localhost:<porta>`.

---

## [L13] L'override Serilog `Microsoft: Warning` nasconde "Now listening on:" → F5 di VS Code non apre il browser

**Contesto:** premendo F5 in VS Code (`.vscode/launch.json`, config `Debug (http)`) il browser doveva
aprirsi su `http://localhost:5242/scalar/v1` ma non succedeva.
**Approccio errato:** dare per scontato che il `serverReadyAction` (`pattern` su
`\bNow listening on:\s+(https?://\S+)`, `uriFormat` `%s/scalar/v1`) bastasse.
**Errore:** il browser non si apre mai; nessun errore evidente.
**Causa:** la riga `Now listening on: …` è emessa da `Microsoft.Hosting.Lifetime` a livello
**Information**, ma il Serilog `MinimumLevel.Override` ha `"Microsoft": "Warning"` → la riga è
**soppressa**. Senza quella riga nello stdout, VS Code non rileva mai il "server ready" e non lancia
il browser.
**Soluzione:** aggiungere un override mirato `"Microsoft.Hosting.Lifetime": "Information"` in
`appsettings.json` (tenendo il resto di `Microsoft` a Warning). Così riappaiono solo i messaggi di
lifecycle (`Now listening on`, `Application started`, `Hosting environment`) e il `serverReadyAction`
matcha. Correlato a `[L02]` (Serilog rende visibili/invisibili i log `Microsoft.*`).

---

## [L14] Idempotency: replay verbatim → serve bufferizzare la risposta in un middleware (non un filtro)

**Contesto:** middleware `Idempotency-Key` sui POST (store + replay della prima risposta). Dettagli in
`.claude/context/idempotency.md`.
**Approccio errato:** pensare di catturare la risposta da rigiocare con un action/result filter MVC.
**Errore/causa:** al momento del filtro l'header **`Location`** di un `CreatedAtActionResult` non
esiste ancora (è generato durante la *result execution*, dall'`UrlHelper`), e il body non è ancora
serializzato. Un filtro non vede quindi la risposta reale da memorizzare.
**Soluzione:** **middleware** che avvolge l'intera pipeline e cattura la risposta vera bufferizzando
lo stream: `var original = Response.Body; Response.Body = buffer; await next(); ...` poi si copia il
buffer su `original` (per il client) e si memorizzano `StatusCode` + header `Location` + body. Per il
fingerprint della richiesta: `Request.EnableBuffering()`, leggere il body, **riavvolgere a 0** così il
model binding lo rilegge.

**Note aggiuntive:**
- **Memorizzare solo 2xx–4xx, mai 5xx**: un errore transitorio (DB giù) deve restare ritentabile;
  cache-arlo bloccherebbe i retry legittimi.
- **`IDistributedCache` non è registrato da FusionCache.** `WithDistributedCache(new RedisCache(...))`
  configura solo FusionCache, non mette un `IDistributedCache` nel DI. Per lo store dell'idempotency
  va registrato a parte: `AddDistributedMemoryCache()` oppure `AddStackExchangeRedisCache(...)`.
- **`Configure<T>(IConfiguration)`** richiede il pacchetto `Microsoft.Extensions.Options.ConfigurationExtensions`
  (come il binder in `[L11]`).

---

## [L15] Rate limiter nativo .NET: ordine pipeline, config lazy, Retry-After e isolamento nei test

**Contesto:** rate limiter `AddRateLimiter`/`UseRateLimiter` con policy sliding-window read/write +
429 ProblemDetails. Dettagli in `.claude/context/rate-limiting.md`.

**Ordine middleware:** `app.UseRateLimiter()` va **dopo `UseAuthorization()`** (la partizione per
utente legge il claim, che senza auth non c'è) e **prima** del middleware di idempotency (rifiuta
presto, prima del buffering del body). `QueueLimit = 0` → 429 immediato invece di accodare.

**Config letta troppo presto (il bug):** leggere le opzioni alla **registrazione**
(`configuration.GetSection(...).Get<RateLimitingOptions>()` dentro `AddApiRateLimiting`) ignora gli
override di `WebApplicationFactory`: in minimal hosting le sorgenti `ConfigureAppConfiguration` del
test sono applicate durante `builder.Build()`, **dopo** che i servizi sono già registrati → il limiter
vedeva sempre `appsettings.json`, mai i limiti minuscoli del test. **Soluzione:** leggere le opzioni
**a tempo di richiesta** via `IOptions<RateLimitingOptions>` dentro il partitioner (binding lazy,
post-build) — è anche design migliore (niente cattura eager).

**Retry-After non garantito:** la sliding window non sempre espone il metadata `RetryAfter` nel lease.
**Soluzione:** in `OnRejected`, fallback alla finestra della policy che ha respinto (ricavata
dall'endpoint via `EnableRateLimitingAttribute.PolicyName`), così l'header c'è sempre. NB: il 429 è
**RFC 6585 §4**, non RFC 9110.

**Test che si sporcano a vicenda:** il limiter è un **singleton in-memory** condiviso dalla factory
della collection, e l'auth di test usa un `NameIdentifier` fisso → tutte le richieste cadono nella
stessa partizione. Senza accorgimenti la suite cumulativa supererebbe i limiti reali e farebbe 429
"a caso". **Soluzione:** (1) la factory base alza i limiti ad altissimi (rate limiting neutro per il
resto della suite); (2) i test del rate limiter li riabbassano via `WithWebHostBuilder` (host nuovo =
limiter pulito) e isolano la partizione con un'identità distinta per test (header `X-Test-User`).

---

## [L16] API versioning (Asp.Versioning) + native OpenAPI: pacchetti, doc per versione, endpoint neutri

**Contesto:** versioning per segmento URL con un documento OpenAPI per versione. Dettagli in
`.claude/context/api-versioning.md`.

**`Asp.Versioning.OpenApi` non esiste su NuGet.** La via "ufficiale" dei doci recenti
(`.AddOpenApi(o => o.Document.AddScalarTransformers())` + `app.MapOpenApi().WithDocumentPerVersion()`
+ `app.DescribeApiVersions()`) usa un pacchetto **non pubblicato**. **Soluzione** con i pacchetti reali
(`Asp.Versioning.Mvc` + `Asp.Versioning.Mvc.ApiExplorer`, v10 per net10): l'`ApiExplorer`
(`GroupNameFormat="'v'VVV"`) assegna a ogni operazione un `GroupName` (`"v1"`/`"v2"`), e il documento
OpenAPI **nativo** dello stesso nome (`AddOpenApi("v1")`, `AddOpenApi("v2")`) include solo le
operazioni di quella versione. Scalar: ciclo su `ApiVersions.All` + `options.AddDocument(group)`.

**I transformer OpenAPI esistenti vanno applicati a OGNI documento di versione**, non al solo default:
estratti in un `OpenApiOptions.AddPlaygroundTransformers()` condiviso, invocato per ogni
`AddOpenApi(group, ...)`. Rimossa la vecchia `AddOpenApi(o => ...)` senza nome.

**Controller non versionati si rompono col versioning attivo.** Con `AssumeDefaultVersionWhenUnspecified
= false`, un controller MVC senza `[ApiVersion]` (es. il `ThrowingTestController` dei test su
`/__tests__`) non risolve la versione → l'endpoint non è raggiungibile e i test a valle falliscono.
**Soluzione:** `[ApiVersionNeutral]` sugli endpoint fuori dal versioning (rotte senza segmento di
versione). Gli health check via `MapHealthChecks` non sono interessati (non sono controller MVC).

**Versione sconosciuta col segmento URL → 404, non 400.** La versione è parte della rotta: `/api/v3/...`
non matcha alcun endpoint → 404 (con i reader header/query si avrebbe 400 + `api-supported-versions`).
È una proprietà dello schema, non un bug — i test lo asseriscono come tale.

---

## [L17] Optimistic concurrency: OriginalValue del token, ETag header-only vs cache L2, scritture pre-esistenti

**Contesto:** rowversion EF Core esposta come ETag, `If-Match` su PUT/DELETE → 412/428. Dettagli in
`.claude/context/optimistic-concurrency.md`.

**Errori e cause:**
- **"Find-then-update" non rileva mai il conflitto.** Il pattern `FindAsync` → modifica campi →
  `SaveChanges` carica l'`OriginalValue` della rowversion **dalla riga appena letta**: l'`UPDATE` esce
  con `WHERE Id=@id AND RowVersion=@valoreAppenaLetto`, che combacia **sempre** → 0 conflitti. → Bisogna
  forzare `Entry(existing).Property(b => b.RowVersion).OriginalValue = <versione attesa dal client>`
  (arrivata da If-Match). Solo così l'UPDATE è condizionato alla versione del client; stale → 0 righe →
  `DbUpdateConcurrencyException`. (Confermato da Context7 su `/dotnet/efcore`.)
- **ETag header-only (`[JsonIgnore]`) vs cache distribuita.** Il token di versione sta sul DTO ma è
  `[JsonIgnore]` (solo header, non nel body). FusionCache **L1 in memoria** tiene l'oggetto vivo → il
  token sopravvive. Ma con **L2 Redis** (config-gated, oggi spento) il DTO viene **serializzato** e
  `[JsonIgnore]` lo strippa → su un hit servito da L2 il token sparisce e l'ETag ricade sull'hash della
  rappresentazione → la concorrenza salta in modo silenzioso. → Limite consapevole finché L2 è inattivo;
  fix futuro: serializzare il token nel payload di cache quando L2 è attivo. La serializzazione di trasporto
  (HTTP) e quella di cache (L2) **non sono la stessa cosa**: un attributo che le governa entrambe è una trappola.
- **`If-Match` obbligatorio = breaking change sui test esistenti.** Rendendo `If-Match` necessario,
  tutti i PUT/DELETE che non lo mandavano iniziano a rispondere **428**, non 200/404. → Adeguati i test
  (GET dell'ETag → If-Match); per i casi "not found" si manda un If-Match **dummy ben formato** (base64
  valido) così si supera il check di precondizione e si raggiunge il 404. Nota di precedenza: 401/403
  (auth) e 400 (validazione del body via filtro) **precedono** l'action, quindi precedono il 428.

**Soluzione:** vedi `BookRepository.UpdateAsync/DeleteAsync`, `ETagResultFilter`,
`PreconditionExceptionHandler` e i test in `tests/.../Concurrency/OptimisticConcurrencyTests.cs`.

---

## [L18] OpenTelemetry: SDK solo in Api, EF beta, bridge Serilog, listener globali nei test

**Contesto:** traces + metrics + logs via OTLP, auto-instrumentation + source/meter custom. Dettagli in
`.claude/context/opentelemetry.md`.

**Errori e cause:**
- **Strumentare il business senza far entrare l'SDK nei layer interni.** Mettere l'SDK OpenTelemetry (o gli
  exporter) in Application/Domain violerebbe i NetArchTest (no AspNetCore/EF/Infra). → Si strumenta con le
  **primitive BCL** (`System.Diagnostics.ActivitySource`/`Metrics.Meter`) in `BooksDiagnostics` (Application);
  l'SDK e `AddSource`/`AddMeter` stanno **solo** nella composition root (Api). `System.Diagnostics.DiagnosticSource`
  è transitivo via `Microsoft.Extensions.Caching.Hybrid` (nessun PackageReference esplicito necessario).
- **EF Core instrumentation è -beta** (`1.15.1-beta.1`): semantic convention DB sperimentali, nomi/attributi
  span instabili. → Inclusa come scelta consapevole (span SQL nelle trace); i test asseriscono lo span DB in
  modo **soft** (Kind=`Client` nello stesso trace, non il nome esatto).
- **Log via Serilog, non il logging provider OTel.** Il progetto è Serilog-centrico → si usa
  `Serilog.Sinks.OpenTelemetry` (config-gated nel lambda `UseSerilog`): riattacca `TraceId`/`SpanId` e porta
  le property del `LogContext` (incluso il `CorrelationId`). NON chiamare `.WithLogging()` sull'SDK (doppio
  canale di log). `UseOtlpExporter()` cross-cutting copre traces+metrics in un colpo (DRY), ma è incompatibile
  con `AddOtlpExporter` per-segnale.
- **`MetricCollector<T>` per testare le metriche** (`Microsoft.Extensions.Diagnostics.Testing`): il parametro
  è `meterScope` (non `scope`); con un `Meter` creato via `new Meter(name, version)` lo scope è `null`. Il
  contatore è un **singleton di processo**: altri test lo incrementano in parallelo → asserire il
  deterministico (ogni misura = 1, `Count >= n`), non un totale esatto.
- **`ActivityListener` è process-global.** Nei test cattura span anche da altri test in parallelo. → Unit:
  filtrare per tag univoco (`book.id`) + `ConcurrentBag`. Integration: i test della collection "Integration"
  girano **in serie**, quindi la finestra `using (listener)` non si sovrappone; comunque `Dispose` per test.
- **traceId W3C solo con un listener attivo.** Senza OTel `Activity.Current` può essere null e il `traceId`
  dei ProblemDetails ricade su `TraceIdentifier` (non-W3C). Con l'AspNetCore instrumentation attiva,
  `Activity.Current.Id` è in formato W3C (55 char, `00-…`): un test lo asserisce come guardia di "OTel attivo".
- **Filtrare gli endpoint di infrastruttura** (`/health`, `/openapi`, `/scalar`) dalle trace (rumore ad alta
  frequenza) via `AddAspNetCoreInstrumentation(o => o.Filter = …)`.

**Soluzione:** vedi `Api/Extensions/OpenTelemetryExtensions.cs`, `Application/Diagnostics/BooksDiagnostics.cs`,
il bridge in `Program.cs` e i test in `tests/.../Observability/` + `Tests/Diagnostics/BooksDiagnosticsTests.cs`.

---

## [L19] Resilience HTTP (Polly v8 / Microsoft.Extensions.Http.Resilience): timeout, predicato transient, opzioni lazy, test

**Contesto:** pipeline di resilienza esplicita (retry/circuit-breaker/timeout) su una dipendenza esterna
(Open Library) come HttpClient tipizzato. Dettagli in `.claude/context/resilience.md`.

**Errori e cause:**
- **`HttpClient.Timeout` combatte col timeout della pipeline.** Lasciare il default (100s) mette un timeout
  *grezzo e non ritentabile* **sopra** la pipeline: scade fuori da Polly, niente retry, niente
  `TimeoutRejectedException`. → Impostare `httpClient.Timeout = Timeout.InfiniteTimeSpan` e delegare **tutti** i
  timeout (per-tentativo + totale) alla pipeline.
- **Versione del pacchetto vs baseline `NU1605`.** L'ultima `Microsoft.Extensions.Http.Resilience` (10.6.0)
  tira transitivi `Microsoft.Extensions.*` 10.6.x in conflitto col baseline **10.0.0** pinnato (downgrade come
  errore, come in [L11]). → Pinnare **10.0.0** (allineato): porta `Polly.Core` 8.4.2 e `Microsoft.Extensions.Http`
  10.0.0 senza conflitti.
- **`ShouldHandle` di default = transitori.** Gli option type **Http** (`HttpRetryStrategyOptions`,
  `HttpCircuitBreakerStrategyOptions`) hanno `ShouldHandle` = `HttpClientResiliencePredicates.IsTransient`
  (5xx/408/429/`HttpRequestException`/`TimeoutRejectedException`): i **4xx non vengono ritentati** *gratis*. Non
  reimpostarlo a mano se non serve. Il timeout usa `HttpTimeoutStrategyOptions`/`AddTimeout(TimeSpan)` (nessun
  predicato).
- **Retry esaurito NON lancia: restituisce l'ultimo outcome.** Dopo i tentativi su un 5xx, la strategia retry
  ritorna l'ultima `HttpResponseMessage` (non-2xx), non un'eccezione. → Il client deve controllare
  `IsSuccessStatusCode` e tradurre lui il fallimento in `ExternalServiceUnavailableException`. Le eccezioni vere
  (`BrokenCircuitException`/`TimeoutRejectedException`/`HttpRequestException`) si catturano a parte.
- **`BrokenCircuitException` (Polly 8.4.2) non espone `RetryAfter`.** → L'handler 503 ricade su una
  `BreakDuration`/fallback configurata per l'header `Retry-After`.
- **Opzioni lette troppo presto = override di test ignorati** (stesso bug di [L15]). Leggere le opzioni alla
  registrazione ignora gli override di `WebApplicationFactory`. → Usare l'overload **con `context`** di
  `AddResilienceHandler` e leggere `IOptionsMonitor<T>.CurrentValue` **lazy** dentro il configuratore (post-build).
- **Testare la pipeline reale senza rete.** Costruire il client con la **registrazione di produzione**
  (`AddBookPopularityClient` + config in-memory minuscola) e poi `ConfigurePrimaryHttpMessageHandler(() => stub)`
  (l'ultima registrazione vince): si esercita la pipeline vera, non un mock. Il **circuit breaker fa fail-fast
  senza colpire il transport** → asserire che il contatore di invocazioni dello stub **non** aumenta è la prova
  che il circuito è aperto. La sola unit dei timeout è robusta se asserisce il tipo (`TimeoutRejectedException`
  come inner) e `Invocations >= 1`, non un conteggio esatto (il predicato transient può ritentare o no).

**Soluzione:** vedi `Infrastructure/Popularity/BookPopularityRegistration.cs` (pipeline + opzioni lazy),
`OpenLibraryPopularityClient.cs` (traduzione errori), `Api/ErrorHandling/ExternalServiceUnavailableExceptionHandler.cs`
(503 + Retry-After) e i test in `tests/.../Popularity/`.

---

## [L20] Cachare una chiamata esterna: i factory timeout della cache preemptano la pipeline di resilienza

**Contesto:** cache della risposta di Open Library (popolarità) davanti alla pipeline di resilienza. Dettagli
in `.claude/context/resilience.md` (sez. *Caching della chiamata esterna*).

**Errori e cause:**
- **`FactoryHardTimeout` globale (2s) abortisce la chiamata esterna su una miss fredda.** Le
  `DefaultEntryOptions` di FusionCache sono tarate sui books (factory DB veloce): `FactorySoftTimeout=100ms`,
  `FactoryHardTimeout=2s`. Riusarle per la popolarità farebbe scattare il hard timeout **a 2s**, *prima* che la
  pipeline di resilienza (3s attempt / 10s total) faccia i suoi retry → resilienza **silenziosamente
  neutralizzata** su ogni miss fredda. → Per la popolarità servono entry options con **factory timeout
  infiniti** (`FactorySoftTimeout = FactoryHardTimeout = Timeout.InfiniteTimeSpan`): il budget di timeout lo
  governa la pipeline, non la cache.
- **L'astrazione `HybridCache` non basta → la cache va in Infrastructure.** `HybridCacheEntryOptions` espone
  **solo** `Expiration`/`LocalCacheExpiration`: NON i factory timeout né il fail-safe (concetti *di
  FusionCache*). Per passarli serve `IFusionCache` (concreto) → che per regola NetArchTest vive solo in
  Infrastructure. Quindi, a differenza di `CachingBooksService` (Application, HybridCache), il decoratore
  `CachingBookPopularityClient` sta in **Infrastructure**. "Due HybridCache con parametri diversi" non
  risolve: quei parametri non sono parametri di HybridCache. L'astrazione che conta resta pulita (Application
  vede solo `IBookPopularityClient`); il decoratore wrappa il **client concreto** (typed client).
- **Fail-safe = degrade-to-stale (cache come resilienza).** Con `IsFailSafeEnabled=true` +
  `FailSafeMaxDuration` lunga, se la factory (chiamata resiliente) **lancia** e l'entry è scaduta ma entro la
  finestra, FusionCache serve l'ultimo valore buono invece di propagare il 503. Il 503 resta solo per **cache
  fredda + dipendenza giù**. Conseguenza sui test: il test del 503 deve girare su cache **fredda** (host
  separato via `WithWebHostBuilder` = FusionCache nuova).
- **Negative caching senza cachare il null.** Per *non* scrivere il "no match" mantenendo il single-flight, nel
  factory: `ctx.Options.SkipMemoryCacheWrite = true; ctx.Options.SkipDistributedCacheWrite = true;` (FusionCache
  adaptive caching). Con caching ON il null si cacha normalmente (GetOrSet cacha anche null).
- **Decorare = i test della pipeline devono risolvere il CONCRETO.** Resa `IBookPopularityClient` = decoratore
  di cache, i test della sola resilienza vanno fatti risolvendo `OpenLibraryPopularityClient` (concreto, senza
  cache); l'override del primary handler diventa `AddHttpClient<OpenLibraryPopularityClient>()`. Mirror di come
  i books-test userebbero il `BooksService` concreto, non il decoratore.
- **Flush del tag anche per la popolarità** ([L11] esteso): nel reset della factory di test, oltre al tag
  `books`, svuotare `IFusionCache.RemoveByTagAsync("popularity")` — la cache è condivisa nella collection.

**Soluzione:** vedi `Infrastructure/Popularity/CachingBookPopularityClient.cs` (entry options + fail-safe),
`PopularityCacheKeys.cs`, la registrazione in `BookPopularityRegistration.cs` e i test in
`tests/.../Popularity/CachingBookPopularityClientTests.cs`.

---

## [L21] Background processing (`BackgroundService` + `Channel`): eccezione che abbatte l'host, scope-per-item, test deterministici

**Contesto:** arricchimento popolarità asincrono (Tier 4 step 1). Producer event-driven sulle write → coda
bounded su `Channel` → `BackgroundService` consumer. Dettagli in `.claude/context/background-processing.md`.

**Errori e cause:**
- **Un'eccezione non gestita in `ExecuteAsync` ferma l'INTERO host.** Da .NET 6 il default è
  `BackgroundServiceExceptionBehavior.StopHost`: se il processing di un item lancia e l'eccezione esce dal loop,
  l'app si spegne. → La base `BackgroundQueueWorker<T>` avvolge **ogni item** in try/catch: l'errore è loggato +
  contato (`background.tasks.failed`) e il loop **prosegue**. Un item velenoso non fa cadere il worker né l'host.
- **Captive dependency: scoped dentro un singleton.** `AddHostedService` registra il worker come **singleton**;
  iniettarci `DbContext`/repository (scoped) li congelerebbe per tutta la vita del processo (bug di vita +
  thread-safety). → `IServiceScopeFactory.CreateScope()` **per item**, e si risolvono le scoped dentro lo scope.
- **Backpressure: bloccare la write o scartare?** Coda **bounded** + `TryWrite` non bloccante: piena → drop +
  metrica (`dropped`), la richiesta HTTP non rallenta mai. Conscio: best-effort, sotto sovraccarico si perde
  qualche arricchimento. L'alternativa (`WriteAsync` che attende) rallenterebbe la write.
- **`await` vietato in un filtro `catch when`.** Il fallback snapshot in `BookPopularityService` non può fare
  `catch (...) when (await ...)` (CS error). → si cattura, si fa il lookup **dentro** il catch e si fa `throw;`
  (rethrow) se non c'è snapshot → resta 503.
- **Test async = flaky se aspetti "a tempo".** Mai `Task.Delay(n)` poi assert. → **unit**: avvia il worker
  (`StartAsync`), accoda, e attendi un `TaskCompletionSource` segnalato dentro `ProcessAsync` (barriera
  deterministica; per "non deve succedere X" usa un secondo item come barriera FIFO). **integration**: polling
  con timeout sullo store. Il tempo nel worker si controlla iniettando un `TimeProvider` fisso (la base risolve
  `TimeProvider` dallo scope).
- **WebApplicationFactory avvia gli hosted service** → l'integration test esercita il worker reale; la tabella
  `BookPopularitySnapshots` nasce da `EnsureCreated` (è nel modello EF), e il reset della factory la svuota
  (ordine FK / cascade) come già per cache+DB [L11].
- **Nuovo `Meter`/`ActivitySource` vanno registrati per nome** in `AddApiObservability` (`AddSource`/`AddMeter`),
  altrimenti metriche/trace del background non vengono esportate. Lo span di processing si aggancia al
  `ActivityContext` catturato all'enqueue → correlazione con la trace della write.
- **Il fallback snapshot cambia la semantica del 503.** Ora `GET popularity` dà 503 **solo** se il live fallisce
  *e* non c'è snapshot. I test "dipendenza giù → 503" devono partire **senza** snapshot (libro seedato diretto,
  nessun enqueue) — restano verdi proprio perché il seed diretto non accoda nulla.
- **Debolezza voluta = at-most-once.** Coda in-memory, enqueue non transazionale con la write: item persi al
  crash, drop su coda piena. Accettato (read normale è fresco, snapshot solo per outage) → è il **movente
  dell'Outbox** (step 2).

**Soluzione:** vedi `Infrastructure/BackgroundProcessing/` (`ChannelBackgroundTaskQueue`, `BackgroundQueueWorker`,
`PopularityEnrichmentWorker`), la registrazione in `Infrastructure/DependencyInjection.cs` (`AddBackgroundProcessing`),
il producer in `Application/Services/BooksService.cs`, il fallback in `BookPopularityService.cs`, la regola in
`tests/.../ArchitectureTests/LayerDependencyTests.cs` e i test in `tests/.../BackgroundProcessing/` +
`tests/.../IntegrationTests/Popularity/PopularityEnrichmentTests.cs`.

---

## [L22] Transactional outbox (senza broker, PR-1): atomicità con chiave IDENTITY, processore separato dal loop, test deterministici

**Contesto:** Tier 4 step 2 — l'arricchimento popolarità passa da coda in-memory (at-most-once, [L21]) a **outbox
transazionale** (at-least-once durevole). PR-1 senza broker: il dispatcher consegna in-process. Dettagli in
`.claude/context/outbox.md`.

**Errori e cause:**
- **Outbox ≠ broker.** L'Outbox è un pattern *lato DB* (riga messaggio nella stessa transazione della write); il
  broker è solo il trasporto. Si può fare (e si è fatto) l'Outbox **senza** broker → at-least-once subito, ASB poi.
- **Atomicità con chiave store-generated.** Il `BookId` è IDENTITY: non esiste prima dell'INSERT, quindi non si
  può materializzare la riga outbox *prima* del `SaveChanges`. → `BeginTransaction → SaveChanges (Id assegnato) →
  Add riga outbox con l'Id → SaveChanges → Commit`. Le due INSERT committano insieme (crash prima del commit =
  rollback di entrambe). Il `BookRepository` riceve una `Func<int, IntegrationEvent>` valutata con l'Id assegnato.
  (Un `SaveChangesInterceptor` è pulito **solo** con chiavi client-generated; qui no → transazione esplicita.)
- **Marcare `ProcessedAt` solo a successo.** Marcarlo dopo un semplice enqueue/relay (fire-and-forget)
  reintrodurrebbe l'at-most-once: il processore esegue il lavoro e marca **solo** se è andato a buon fine →
  at-least-once. Serve un consumer **idempotente** (lo snapshot è 1:1 col libro → rielaborare è sicuro).
- **PITFALL TEST (il più costoso): un hosted service che polla in continuo + DB condiviso = flaky.** La
  `WebApplicationFactory` è condivisa da tutta la collection e **avvia gli hosted service**. Un `OutboxDispatcher`
  che polla ogni N ms sul DB condiviso: (a) **corre con `EnsureCreated`** allo startup → `Invalid object name
  'OutboxMessages'` finché lo schema non c'è; (b) **interferisce fra i test** (processa righe di altri test,
  `ResetDatabaseAsync` cancella mentre lui processa) → set di fallimenti **diverso a ogni run**. Il vecchio worker
  [L21] non l'aveva perché era *event-driven* (lavorava solo all'enqueue del test). → **Separare l'unità di lavoro
  (`OutboxProcessor.ProcessPendingAsync`, scoped) dal loop di hosting (`OutboxDispatcher : BackgroundService`)**:
  in test si **disattiva l'hosted dispatcher** (si rimuove il descriptor `IHostedService`) e si **pilota il
  processing esplicitamente** (`DrainOutboxAsync`) → deterministico, niente polling/timeout, niente interferenza.
  Per testare anche il **loop di hosting reale** in modo deterministico: una factory **dedicata** (`DisableOutboxDispatcher
  => false`) con **container isolato** (nessuna interferenza) e attesa dell'esito con **timeout generoso** (l'esito
  *avverrà* in &lt;1s); la si tiene nella **collection serializzata** per non incrociare il listener OTel globale [L18].
- **Flaky pre-esistente smascherato.** Il test OTel `CreateBook…InSameTrace` falliva ~2/3 (verificato anche su
  `main`): lo span **server** ASP.NET Core si chiude *poco dopo* che il client riceve la risposta, a volte dopo lo
  smontaggio del listener globale → non catturato. Non era una regressione dell'outbox, ma il lavoro extra sul
  path di write ne aumentava la frequenza. → attendere lo span server (poll breve) **prima** di smontare il
  listener, dentro il `using` (vedi anche [L18]).
- **Parità DACPAC ↔ modello EF per l'indice filtrato.** L'indice `WHERE ProcessedAt IS NULL` va dichiarato anche
  in EF (`HasIndex(...).HasFilter(...)`) così `EnsureCreated` (test) crea lo stesso oggetto del DACPAC (prod).

**Debolezza voluta (→ PR-2):** consegna **mono-processo** in-process e dispatcher a **polling** (un solo
processo; competing-consumers multi-istanza richiederebbe lock di riga `UPDLOCK/READPAST` o un broker). È il
movente del broker **Azure Service Bus**: pubblicazione dietro la stessa astrazione + consumer disaccoppiato.

**Soluzione:** vedi `Application/Outbox/` (`IntegrationEvent`, `PopularityEnrichmentRequested`),
`Application/Interfaces/IPopularityEnricher.cs`, `Infrastructure/Outbox/` (`OutboxProcessor`, `OutboxDispatcher`,
`OutboxMessageFactory`, `OutboxOptions`), `Infrastructure/Popularity/PopularityEnricher.cs`, la scrittura
transazionale in `Infrastructure/Repositories/BookRepository.cs`, la registrazione in
`Infrastructure/DependencyInjection.cs` (`AddOutboxProcessing`), `DrainOutboxAsync`/`DisableOutboxDispatcher` in
`PlaygroundApiFactory` e i test in `tests/.../IntegrationTests/Outbox/` (`OutboxProcessingTests` +
`OutboxDispatcherHostTests` per il loop reale) + `tests/.../Popularity/PopularityEnricherTests.cs`.

---

## [L23] Containerizzazione dell'API: chiseled non-root, schema via DACPAC, fail-fast config, arm64

**Contesto:** Tier 5 — Dockerfile + docker-compose per l'API (prima Docker serviva solo ai test via
Testcontainers). Dettagli in `.claude/context/docker.md`.

**Errori e cause:**
- **Testcontainers ≠ Dockerfile ≠ compose.** Testcontainers containerizza *la dipendenza* (SQL) **per i
  test** (app in-process); il Dockerfile containerizza *l'app* come artefatto; compose orchestra lo *stack
  reale* in locale. Sono complementari: il Dockerfile **non** tocca come girano i test.
- **Chiseled = niente shell.** La runtime chiseled è distroless-style: nessun `bash`/`curl`. → **niente
  `HEALTHCHECK` curl-based** nel Dockerfile (fallirebbe sempre); le probe sono **HTTP esterne**
  (compose/orchestratore via `/health/live`). Stesso motivo: niente `docker exec bash`.
- **Chiseled `-extra` per SqlClient (scoperto nella prova E2E vera).** La chiseled "liscia"
  (`aspnet:10.0-noble-chiseled`) **non include ICU** → .NET parte in *Globalization Invariant Mode*, e
  **`Microsoft.Data.SqlClient` (EF Core SqlServer) NON lo supporta**: l'app si avvia e `/health/live` è
  200, ma **ogni query DB esplode** con `Globalization Invariant Mode is not supported` (→ `/health/ready`
  503, endpoint dati 500). Lo smoke test, che colpisce solo `/health/live`, **non** lo prende: serve la
  prova E2E col DB. Fix: base **`aspnet:10.0-noble-chiseled-extra`** (chiseled + ICU + tzdata, sempre
  non-root/no-shell). Asserito nel contract test.
- **Porta host 1433 spesso occupata.** Un SQL Server / Azure SQL Edge locale tiene già `0.0.0.0:1433`
  (`port is already allocated` allo `up`). L'API parla al DB via rete interna (`db:1433`), quindi la
  pubblicazione sull'host serve solo a client esterni → resa configurabile: `ports: ["${SQL_HOST_PORT:-1433}:1433"]`
  (override con `SQL_HOST_PORT` in `.env`).
- **Porta non privilegiata.** Da utente **non-root** (`USER $APP_UID`, UID 64198, default dell'immagine) non
  si fa bind sotto la 1024 → l'app ascolta su **8080** (`ASPNETCORE_HTTP_PORTS=8080`), non 80.
- **Production non parte senza config — di proposito.** Fuori da Development l'app fa **fail-fast esplicito**
  (`StartupConfigurationValidator` in testa a `Program.cs`): se mancano `ConnectionStrings:Default` o
  `AzureAd:ClientId/TenantId/Audience` lancia elencando **tutte** le chiavi mancanti + la forma env var
  (`AzureAd__ClientId`, …). Gate `!IsDevelopment` **condiviso** col bypass auth ([L12]) → test e compose
  girano in Development e non sono toccati. La connection string mancante prima falliva *lazy* (alla prima
  query/`/health/ready`), non allo startup e senza dire cosa mancava.
- **compose in Development per la DX.** L'immagine di default è Production (12-factor), ma il compose la fa
  girare in Development: così in locale hai Scalar UI + auth BYPASS + niente HTTPS redirect senza configurare
  Entra. (Lo smoke test per lo stesso motivo gira in Development: `/health/live` deve rispondere su HTTP.)
- **Schema: DACPAC, non `EnsureCreated`.** Un servizio `db-migrations` one-shot **riusa `deploy.sh`**
  (`sqlpackage publish`, seed incluso) e l'`api` lo attende con `depends_on: condition:
  service_completed_successfully` (+ `db` `service_healthy`). Far creare lo schema all'app (EF
  `EnsureCreated`) bypasserebbe la source of truth DACPAC e perderebbe il seed.
- **arm64 (Apple Silicon).** L'immagine `mssql/server` è **solo amd64** → serve `platform: linux/amd64`
  (emulazione Rosetta, più lenta ma funziona). Vincolo già implicito coi Testcontainers.
- **Segreti fuori dall'immagine e dal compose.** Il `.dockerignore` esclude `appsettings.Development.json`
  (la connection string locale **non** si imbarca: in container arriva da env). La SA password sta in `.env`
  (gitignored), nel compose solo `${MSSQL_SA_PASSWORD}`; nel test contract si asserisce che la dev password
  reale non compaia mai.
- **Healthcheck compose: escape `$`.** In `test: ["CMD-SHELL", "… -P \"$$MSSQL_SA_PASSWORD\" …"]` il `$$`
  evita l'interpolazione di compose così la var la espande la shell *dentro* il container.

**Soluzione:** vedi `Dockerfile`, `.dockerignore`, `database/Dockerfile`, `docker-compose*.yml`,
`.env.example`, `src/WebApiPlayground.Api/Configuration/StartupConfigurationValidator.cs` e i test in
`tests/WebApiPlayground.DockerTests/` (contract + smoke). Documentazione: `.claude/context/docker.md`.

---

## [L24] Outbox + broker Azure Service Bus (PR-2): trasporto astratto config-gated, handoff at-least-once, emulatore nei test

**Contesto:** Tier 4 step 2 — PR-2 aggiunge il **broker** all'outbox [L22]. L'arricchimento popolarità si
disaccoppia: l'outbox **pubblica** su Service Bus, un **consumer** separato arricchisce. Dettagli in
`.claude/context/outbox.md`.

**Decisioni e cause:**
- **Trasporto dietro un'astrazione, non un `if` nel processore.** Il `OutboxProcessor` non chiama più l'enricher:
  pubblica via `IIntegrationEventPublisher` (Application, BCL pura). Due impl in Infrastructure, scelte dalla
  composition root come Redis/OTLP: `InProcessIntegrationEventPublisher` (default, gestisce subito → identico a
  PR-1) e `ServiceBusIntegrationEventPublisher` (se `ServiceBus` è configurato). Vuoto = in-process → **gira
  ovunque senza Azure**. Regola NetArchTest dedicata: Application non deve dipendere da `Azure.Messaging`/`Azure.Identity`.
- **Routing in un solo posto.** La logica "evento → enricher" (+ span correlato al `traceparent`) sta in un unico
  `IntegrationEventHandler`, riusato dal publisher in-process **e** dal consumer ASB → impossibile divergere. La
  mappa `Type → tipo concreto` è in `IntegrationEventSerialization` (unica sorgente, usata anche dalla riga outbox).
- **`ProcessedAt` = "consegnato al trasporto", non "arricchito".** In modalità ASB l'outbox marca processato quando
  il **broker accetta** il messaggio (durevole): l'arricchimento avviene dopo, nel consumer, con il **suo**
  at-least-once (settlement manuale: `Complete` a successo, `Abandon` → redelivery, oltre `maxDeliveryCount` →
  dead-letter). Consumer **idempotente** (upsert snapshot 1:1 col libro) → redelivery sicura. Conseguenza nota: il
  contatore `background.tasks.processed` conta i *publish* sul broker, non gli arricchimenti del consumer
  (osservabili via metriche ASB + span "Popularity.Enrich").
- **No SAS anche in IaC.** Il modulo `servicebus.bicep` ha `disableLocalAuth: true` (solo AAD) → l'app si autentica
  con managed identity (`FullyQualifiedNamespace` + `DefaultAzureCredential`), nessun segreto. RBAC least-privilege
  sull'ambito **coda**: Data Sender + Data Receiver (non Owner). La connection string SAS serve **solo**
  all'emulatore locale.
- **PITFALL package — downgrade NU1605.** `Azure.Identity` → `Azure.Core` richiede
  `Microsoft.Extensions.Hosting.Abstractions >= 10.0.3`: pinnata a 10.0.0 il restore **fallisce** (warning-as-error
  sul downgrade). → bumpare quella reference a 10.0.3.
- **PITFALL test — emulatore ASB con Testcontainers.** Il modulo `Testcontainers.ServiceBus` avvia l'emulatore
  ufficiale (+ un MsSql di supporto) e ha una coda di default **`queue.1`** (la si usa via `ServiceBus:QueueName`).
  (a) **Ordine:** l'emulatore va avviato **prima** che la `WebApplicationFactory` costruisca l'host (la connection
  string si legge in `AddInfrastructure`); si reimplementa **esplicitamente** `IAsyncLifetime` nella factory
  derivata (rimappa per il tipo derivato) avviando il container e poi delegando a `base.InitializeAsync()`. (b) Il
  bump a Testcontainers **4.12** (tirato da `Testcontainers.ServiceBus`) **deprecat​a** il costruttore senza
  immagine → pinnare il tag esplicito in `MsSqlBuilder(...)`/`ServiceBusBuilder(...)` (anche più riproducibile).
  (c) Il test reale (publish→consume→enrich) sta nella **collection serializzata** [L18] con factory+container
  dedicati; il **seam** (outbox pubblica senza arricchire) è coperto a parte con un publisher **fake**, senza broker.
- **Bicep scritto ma NON deployato.** Senza profilo/subscription Azure il modulo è **validato con `bicep build`**
  (lint/compile) e dai test IaC (`ServiceBusModuleTests`), ma `what-if`/deploy restano da fare alla creazione
  dell'account — documentato come tale, nessun claim di "deployato".
- **ASB è il percorso REALE, non opzionale (scelta di design).** Inizialmente il trasporto era config-gated con
  l'in-process come *default*. Su richiesta si è ribaltato: ASB è il trasporto reale in **docker-compose**
  (emulatore sempre acceso → il giro publisher→coda→consumer gira in locale) e in **Production** (managed
  identity), mentre l'in-process resta **solo** come fallback per il bare `dotnet run` offline (Development).
  Conseguenza: `StartupConfigurationValidator` richiede `ServiceBus:FullyQualifiedNamespace` **fuori da
  Development** (fail-fast, come DB/AzureAd [L23]).
- **PITFALL compose — l'emulatore non è un one-liner.** Serve: (a) un `Config.json` montato in
  `/ServiceBus_Emulator/ConfigFiles/Config.json` che **dichiara la coda** (stesso nome dell'app); (b) un **SQL
  Server di supporto** (l'emulatore lo richiede per il suo stato) — qui si **riusa il container `db`**
  (`SQL_SERVER=db`) per non avere un terzo SQL pesante su arm64; (c) `ACCEPT_EULA=Y`; (d) connection string
  **statica** dell'emulatore `Endpoint=sb://<servizio>;…;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true`
  (host = nome del servizio compose, chiave letterale nota). Solo amd64 → `platform: linux/amd64` su Apple Silicon.
- **PITFALL avvio — `StartProcessingAsync` che lancia abbatte l'host.** Se il broker non è pronto quando il
  consumer parte (ordine di boot in compose), l'eccezione in un `BackgroundService` ferma l'host
  (`BackgroundServiceExceptionBehavior.StopHost`). → il consumer **riprova** lo start con backoff finché il broker
  risponde (robusto anche per outage transitori in prod); `depends_on: servicebus` è solo best-effort.
- **Transitorio atteso allo startup compose.** L'emulatore registra la coda *dopo* aver accettato connessioni:
  per ~1-2s il receive-loop del processore logga `MessagingEntityNotFound` (a livello ERR) finché la coda non
  esiste, poi **si auto-ripristina** da solo (verificato: lo snapshot viene comunque scritto). Si è scelto di
  **non** declassare quel log: un `MessagingEntityNotFound` *persistente* segnala una coda/nome sbagliati e va
  visto. **Verifica E2E reale** (`docker compose up` + POST): outbox `PROCESSED`, snapshot scritto dal consumer.

**Debolezza voluta (→ futuro):** il consumer è in-process nell'API; è già predisposto per lo split in un Worker
(`Microsoft.NET.Sdk.Worker`) — nessuna dipendenza dall'API, riusa `IPopularityEnricher`/`IntegrationEventHandler`.

**Soluzione:** vedi `Application/Outbox/IIntegrationEventPublisher.cs`, `Infrastructure/Outbox/`
(`IntegrationEventHandler`, `IntegrationEventSerialization`, `InProcessIntegrationEventPublisher`, refactor di
`OutboxProcessor`) e `Infrastructure/Outbox/ServiceBus/` (`ServiceBusOptions`, publisher, consumer, registration),
il gating in `DependencyInjection.AddOutboxProcessing`, `infra/modules/servicebus.bicep` + wiring in `main.bicep`,
e i test `tests/.../IntegrationTests/Outbox/` (`OutboxTransportTests` fake + `ServiceBusOutboxTests` emulatore),
`tests/.../Tests/Outbox/` (handler + serializzazione), `tests/.../IacTests/ServiceBusModuleTests.cs`.

---

## [L25] WebApplicationFactory: la config iniettata via `ConfigureAppConfiguration` è INVISIBILE in fase builder

**Approccio errato:** nei test, iniettare con
`builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(...))` valori che `Program.cs` legge
**in fase builder** (prima di `Build()`): `KeyVault:Uri` (letto da `AddKeyVaultIfConfigured`),
`ServiceBus:ConnectionString` (letto eager da `AddInfrastructure` per scegliere il trasporto).

**Errore:** nessuna eccezione — peggio: il valore risulta vuoto in fase builder, il ramo config-gated
non si attiva, e il test **passa per il motivo sbagliato** sul percorso di fallback. Il caso reale:
`ServiceBusOutboxTests` (PR-2) girava sul trasporto **in-process** invece che sull'emulatore ASB —
verde comunque, perché l'handler a valle è lo stesso. Scoperto qui perché col Key Vault il fallback
*non* esiste (conn string solo nel vault) → `EnsureCreated` esplodeva con "connection refused" verso
il DB di `appsettings.Development.json`.

**Causa:** con il minimal hosting la WAF applica i provider registrati via `ConfigureAppConfiguration`
**dopo** le source aggiunte da `Program.cs`: i valori esistono nella config finale (bene per le
opzioni bound lazy via `IOptions`), ma non ancora quando il codice del builder li legge.

**Soluzione:** per i valori letti in fase builder usare **`builder.UseSetting("chiave", valore)`**
(host configuration, applicata prima che `Program.cs` prosegua). E aggiungere un **probe strutturale**
che renda impossibile il falso verde: `Transport_is_really_Azure_Service_Bus` asserisce che
`ServiceBusClient` sia registrato; `KeyVaultEnabledApiFactory` disattiva il ripunto del DbContext
(`OverrideDbContextWithTestContainer => false`) così la conn string può arrivare SOLO dal vault.
Regola pratica: ogni ramo config-gated testato e2e deve avere un'asserzione che il ramo sia DAVVERO attivo.

---

## [L26] Emulatore Key Vault: TLS obbligatorio con contratto fisso, token non validati, NuGet del progetto da evitare

**Contesto:** il config provider Key Vault va provato e2e senza account Azure → emulatore community
`james-gould/azure-keyvault-emulator` (Microsoft non ne pubblica uno) in compose e nei test. Vedi
`.claude/context/keyvault.md` e `docs/keyvault.md`.

**Cose non ovvie (costate tempo o rischio):**
- **L'SDK Azure pretende HTTPS + challenge verification.** Verso l'emulatore servono ENTRAMBI i bypass
  nel `SecretClientOptions`: `DisableChallengeResourceVerification = true` (l'URI non è
  `*.vault.azure.net`) e un transport che accetti il TLS self-signed. Nel codice app sono consentiti
  SOLO in modalità `Emulator`, che è Development-only (guard con errore parlante).
- **Contratto TLS dell'immagine fisso:** cert in `/certs/emulator.pfx` con password `emulator`
  (ENV Kestrel nel Dockerfile dell'emulatore). La password NON è un segreto: è il contratto.
  SAN del cert: deve includere l'hostname con cui lo si raggiunge (`keyvault` sulla rete compose).
- **I token non sono validati** (`SignatureValidator` pass-through, niente check su firma/issuer/
  scadenza): basta un JWT **ben formato** → si minta localmente (in C# e perfino in shell nel seed),
  zero round-trip e zero dipendenza da endpoint ausiliari (`GET /token` esiste ma è legacy).
- **I NuGet del progetto emulatore sono comodi ma invasivi:** il loro modulo Testcontainers
  **installa una CA self-signed nel trust store dell'host** (anche in CI). Evitati: Testcontainers
  liscio + cert generato per-run (`CertificateRequest` nei test, `openssl` one-shot in compose) +
  trust scoped al singolo client. Immagine **pinnata** (`3.1.0`; `3.1.0-arm` nativo per Apple
  Silicon — i due tag sono single-arch: in compose tag e `platform` si cambiano in coppia, vedi
  `.env.example`).
- **Bonus fail-fast:** il `catch` di bootstrap in `Program.cs` faceva solo `Log.Fatal` → il processo
  usciva con **exit code 0** anche quando rifiutava di partire (config mancante, vault giù):
  aggiunto `Environment.ExitCode = 1`, altrimenti orchestratore/CI vedono un'uscita "pulita".

**Soluzione:** vedi `Api/Configuration/KeyVault/`, `tests/.../IntegrationTests/KeyVault/`
(`KeyVaultEmulatorContainer` self-made), `docker/keyvault-emulator/*.sh` e i contract test Docker.

---

## [L27] Gli integration test giravano su uno schema che nessuno deploya (EnsureCreated ≠ DACPAC)

**Approccio errato:** creare lo schema dei test con `EnsureCreated()` (dal modello EF) e fidarsi,
mentre compose/CI/produzione pubblicano lo schema dal **DACPAC** (source of truth dichiarato).

**Errore:** nessuno — è questo il problema. Un drift tra modello EF e progetto SQL (colonna
rinominata solo da una parte, tipo diverso, NOT NULL mancante) lascia la suite **verde** e rompe
solo l'app vera al primo deploy.

**Causa:** due fonti di schema mai confrontate. Il gotcha era già noto a metà (database.md: "dopo un
cambio schema migra anche il DB locale"), ma non esisteva un test che lo facesse rispettare.

**Soluzione:** suite di parità dedicata (`tests/.../IntegrationTests/Database/`):
`DacpacDeployedApiFactory` deploya il pacchetto VERO con **DacFx programmatico**
(`DacServices.Deploy`, niente sqlpackage da installare; il DACPAC è prodotto dalla build della
solution) su un database dedicato del container, e l'app ci gira contro (override
`AppConnectionString`). I test: confronto strutturale colonna-per-colonna (store type +
nullabilità, normalizzando `timestamp`→`rowversion` e le scale di default tipo
`datetimeoffset(7)`), una SELECT reale su OGNI entità mappata, il write-path completo (IDENTITY,
FK, rowversion/If-Match, outbox→snapshot) e la paginazione sul post-deployment seed. Hook
riusabili in `PlaygroundApiFactory`: `OnSqlContainerStartedAsync` + `AppConnectionString`
(`EnsureCreated` su uno schema già deployato è un no-op → i due mondi convivono).

---

## [L28] Pipeline JwtBearer VERA con authority OIDC finta: tre trappole di wiring

**Contesto:** tutta la suite usa il `TestAuthHandler` (claim simulati) → firma/issuer/audience/
lifetime non erano MAI esercitati. `RealJwtAuthTests` fa girare `AddMicrosoftIdentityWebApi` reale
contro `FakeOidcAuthority` (Kestrel in-proc su loopback: discovery + JWKS + chiave RSA per-run).
Trappole incontrate:

- **`RequireHttpsMetadata=false` va impostato nello stage `Configure`, NON `PostConfigure`.** Il
  framework registra un proprio `IPostConfigureOptions<JwtBearerOptions>` che VALIDA il flag e
  lancia ("Authority must use HTTPS"): registrato prima del nostro, gira prima → l'override in
  PostConfigure arriverebbe a eccezione già lanciata (sintomo: **500** su ogni richiesta, non 401).
  Nello stage Configure invece si gira DOPO quello di Identity.Web (ordine di registrazione) e
  PRIMA dei PostConfigure — posto giusto anche per sostituire l'`AadIssuerValidator` (vuole il
  metadata Entra reale) con `ValidIssuer` + validatore standard.
- **Identity.Web rifiuta in AUTENTICAZIONE (401, non 403) i token senza né `scp` né `roles`**: per
  una web API pretende almeno uno dei due claim. Il 403 "autenticato ma senza permesso" si testa
  con uno scope insufficiente (read che tenta una write).
- **`ClockSkew` default = 5 minuti**: i token "scaduti"/"non ancora validi" dei test devono sforare
  di PIÙ (±30 min), o risultano validi.

Bonus sui retry (test `Retry-After` del client di popolarità): la strategy HTTP di Polly **onora
`Retry-After` come delay tra i tentativi** → nei test l'header deve essere piccolo, o il
TotalTimeout scatta prima che l'ultima risposta (quella con l'header) torni al chiamante.

---

## [L29] Stryker.NET: mai solution mode con integration test nella solution, e il rollback che muore sui pattern in espressioni condizionali

**Approccio errato:** `stryker-config.json` con `"solution"` + `"test-projects": [unit]`, contando
che il filtro limitasse il test bed agli unit test.

**Errore/i (run di calibrazione):**
1. la run è durata ~50 minuti e i log mostravano 303 test (TUTTA la suite): in solution mode
   Stryker **arruola ogni test project della solution** (integration coi Testcontainers compresi)
   nonostante `test-projects`. Effetto collaterale: i test time-sensitive falliscono sotto il
   carico dei runner paralleli → "X tests are failing. Outcome will be impacted".
2. `CompilationException: Internal error due to compile error` che ammazza l'intera run: un
   mutante su `ex.RetryAfter is { } hint && hint > TimeSpan.Zero ? hint : …` produce
   "variabile 'hint' già definita / non assegnata" e il **rollback non recupera** (bug noto con le
   pattern-variable dichiarate dentro espressioni condizionali).
3. `CS9234` sull'**interceptor del source generator OpenAPI** (XmlCommentGenerator): il generatore
   intercetta la chiamata `AddOpenApi` in `ApiVersioningExtensions.cs` con un checksum del file —
   mutare QUEL file invalida il checksum e la compilazione mutata esplode (nemmeno il "Safe Mode"
   di Stryker recupera) → il file intercettato va **escluso dal mutate**.
4. Run su Infrastructure: "No project found" — Stryker risolve `--project` solo tra le reference
   **DIRETTE** del test project; Infrastructure arrivava transitiva via Api → aggiunta la
   ProjectReference esplicita in `WebApiPlayground.Tests.csproj`.
5. Minori: il config rifiuta chiavi sconosciute (niente commenti `"//"`); un assert sul TIPO
   esatto dell'implementazione di un package (`RedisCache`) è fragile — sotto la build di Stryker
   risolve l'internal `RedisCacheImpl` → usare `IsAssignableFrom`; Domain (soli POCO) produce
   mutanti senza comportamento osservabile → escluso dalla lista, non è un buco.

**Soluzione:** orchestratore **`tests/run-mutation.sh`**: una run per layer
(Application/Infrastructure/Api; `--project <csproj>`, eseguita dalla dir del progetto unit test →
test bed = SOLI unit test), `mutate` esclude `ApiVersioningExtensions.cs` (interceptor), punteggio
COMBINATO calcolato dai `mutation-report.json` (scritto in `combined-score.json`, usato dal badge).
L'espressione con la pattern-variable è stata riscritta in forma equivalente senza dichiarazione
(`ex.RetryAfter > TimeSpan.Zero ? ex.RetryAfter!.Value : fallback`). In CI: incrementale su PR
(`--since`, **informativa** con `--break-at 0`: il primo collaudo ha mostrato che una soglia fissa
boccia le PR che toccano codice coperto solo da integration test — e una PR che cambia i csproj fa
considerare a `--since` "tutto cambiato"), full solo manuale (`mutation-full.yml`, soglia ratchet
in config) — niente schedule per scelta.

---

<!-- Template per nuove entry:
## [L0N] Titolo breve

**Approccio errato:** ...  
**Errore:** ...  
**Causa:** ...  
**Soluzione:** ...
-->
