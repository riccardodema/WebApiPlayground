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

<!-- Template per nuove entry:
## [L0N] Titolo breve

**Approccio errato:** ...  
**Errore:** ...  
**Causa:** ...  
**Soluzione:** ...
-->
