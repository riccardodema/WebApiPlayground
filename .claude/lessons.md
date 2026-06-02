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

<!-- Template per nuove entry:
## [L0N] Titolo breve

**Approccio errato:** ...  
**Errore:** ...  
**Causa:** ...  
**Soluzione:** ...
-->
