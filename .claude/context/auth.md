# Autenticazione & autorizzazione — Microsoft Entra ID

L'API è protetta con **Microsoft Entra ID** come identity provider. I bearer token JWT sono
validati con [Microsoft.Identity.Web](https://learn.microsoft.com/entra/msal/dotnet)
(`AddMicrosoftIdentityWebApi`, wrapper su JwtBearer). L'autorizzazione distingue **lettura** e
**scrittura** e supporta sia il flusso **delegated** (utente→API) sia **application** (macchina→macchina).

## Modello

| Endpoint | Verbo | Delegated (scope, claim `scp`) | Application (app role, claim `roles`) |
|---|---|---|---|
| `GetBooks` / `GetBookById` | GET | `Books.Read` **o** `Books.ReadWrite` | `Books.Read.All` **o** `Books.ReadWrite.All` |
| `CreateBook` / `DeleteBook` | POST / DELETE | `Books.ReadWrite` | `Books.ReadWrite.All` |

- **401 Unauthorized**: nessun token (o invalido) → `[Authorize]` a livello di controller.
- **403 Forbidden**: token valido ma senza scope/app-permission richiesto.

Semantica garantita dal framework; la policy non concede mai accesso a un token senza il claim giusto
(verificato empiricamente: uno scope/role di sola lettura su un endpoint di scrittura → 403).

## Dove sta nel codice

- `Api/Authorization/BooksPermissions.cs` — nomi di scope e app permission (no stringhe sparse).
- `Api/Authorization/AuthorizationPolicies.cs` — nomi policy (`ReadBooks`, `WriteBooks`).
- `Api/Extensions/AuthenticationExtensions.cs` — `AddApiAuthentication` (Entra/JWT) + `AddApiAuthorization`
  (policy `RequireScopeOrAppPermission`). Segue il pattern `AddApplication`/`AddInfrastructure`.
- `Api/OpenApi/BearerSecuritySchemeTransformer.cs` — espone lo schema Bearer in OpenAPI (campo
  "Authentication" in Scalar).
- `Program.cs` — `AddApiAuthentication`/`AddApiAuthorization` + **`UseAuthentication()` prima di `UseAuthorization()`**.
- `BooksController` — `[Authorize]` sul controller, `[Authorize(Policy = AuthorizationPolicies.ReadBooks/WriteBooks)]` per action.

## Configurazione (`appsettings.json`, sezione `AzureAd`)

```json
"AzureAd": {
  "Instance": "https://login.microsoftonline.com/",
  "TenantId": "<tenant-guid>",
  "ClientId": "<api-app-client-id>",
  "Audience": "api://<api-app-client-id>"
}
```

Sono **GUID, non segreti** (la validazione usa le chiavi pubbliche del tenant): coerente con la
regola "nessun secret nell'IaC". In dev usare user-secrets o `appsettings.Development.json`.
Con `AzureAd` vuoto l'app parte ma i token reali non validano; i test sostituiscono lo schema.

## Setup Entra (una tantum, portale o CLI)

1. **Registra l'app API** → *Expose an API*: Application ID URI `api://<clientId>`, aggiungi gli
   scope delegati `Books.Read` e `Books.ReadWrite`.
2. **App roles** (*App roles* nell'app registration): `Books.Read.All` e `Books.ReadWrite.All`,
   *Allowed member types* = Applications (+ Users se servono ruoli utente). Popolano il claim `roles`.
3. **Assegna**: scope agli utenti/app client (delegated, consenso); app permission alle app daemon
   (admin consent).
4. Compila `AzureAd` con `TenantId`/`ClientId`.

## Provare in locale (Scalar)

`http://localhost:5242/scalar/v1` → campo *Authentication* → incolla un access token Entra valido
(senza prefisso `Bearer `). Senza token → 401; con scope corretto → 200/201.

## Test

I test **non** richiedono un tenant reale: `tests/WebApiPlayground.IntegrationTests/Infrastructure/TestAuthHandler.cs`
è uno schema di autenticazione fittizio impostato come default in `PlaygroundApiFactory`, che
costruisce i claim da header:

- `X-Test-Scope` → claim `scp` (token delegato). Helper `CreateClientWithScope(...)`.
- `X-Test-Roles` → claim `roles` (app permission). Helper `CreateClientWithAppRoles(...)`.
- nessun header → 401. Helper `CreateAnonymousClient()`.

Copertura: matrice 401/403/200/201/204 in `Books/BooksControllerTests.cs` (delegated + application);
unit test reflection sugli attributi in `tests/WebApiPlayground.Tests/Controllers/BooksControllerAuthorizationTests.cs`.
