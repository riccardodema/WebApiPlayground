# Key Vault config provider — segreti a runtime dal vault

**Scopo:** i segreti (connection string DB, ecc.) entrano in `IConfiguration` **dal Key Vault**
all'avvio, invece che da env var/appsettings. Config-gated su `KeyVault:Uri` (vuoto = spento).
Documento utente completo: `docs/keyvault.md`. Pitfalls: `lessons.md` [L25] [L26].

## Mappa dei file

| Cosa | Dove |
|---|---|
| Options (sezione `KeyVault`) + valori credential | `src/WebApiPlayground.Api/Configuration/KeyVault/KeyVaultOptions.cs` |
| Bootstrap provider + credential factory + messaggi fail-fast | `.../KeyVault/KeyVaultConfigurationExtensions.cs` |
| Credential per l'emulatore (JWT mintato, Development-only) | `.../KeyVault/KeyVaultEmulatorCredential.cs` |
| Wiring (PRIMA del validator) | `Program.cs` → `builder.AddKeyVaultIfConfigured()` |
| Hint Key Vault nel fail-fast config | `Configuration/StartupConfigurationValidator.cs` |
| Emulatore in compose (cert one-shot + seed one-shot) | `docker-compose.yml` + `docker/keyvault-emulator/*.sh` |
| Seed del vault REALE | `infra/set-secrets.sh` (wrapper `az keyvault secret set`) |
| Unit test | `tests/WebApiPlayground.Tests/Configuration/KeyVaultConfigurationTests.cs` |
| Integration test (emulatore Testcontainers, e2e) | `tests/WebApiPlayground.IntegrationTests/KeyVault/` |
| Contract test compose/script | `tests/WebApiPlayground.DockerTests/DockerArtifactsContractTests.cs` |
| Regola NetArchTest (SDK KV solo in Api) | `tests/WebApiPlayground.ArchitectureTests/` (`KeyVaultImplementationNamespaces`) |

## Regole di design (non violare)

- **Composition root = Api.** `AddAzureKeyVault` e l'SDK (`Azure.Security.KeyVault`,
  `Azure.Extensions.AspNetCore.Configuration`) vivono SOLO nel layer Api: gli altri layer leggono
  config già risolta (IConfiguration/IOptions) senza sapere da dove viene. Regola NetArchTest.
- **Provider aggiunto per ULTIMO e PRIMA del validator.** Ordine in `Program.cs`:
  `CreateBuilder` → `AddKeyVaultIfConfigured()` → `StartupConfigurationValidator`. Conseguenze:
  i secret del vault **vincono** su appsettings/env var, e soddisfano il fail-fast.
- **Credential ESPLICITA, mai `DefaultAzureCredential`**: `ManagedIdentity` (default; `ClientId`
  per user-assigned) / `AzureCli` (locale vs vault reale) / `Emulator` (SOLO Development, guard
  con errore parlante). Confronti case-insensitive.
- **Ogni fallimento di bootstrap → eccezione parlante** (uri, credential, cause probabili per
  403/auth/rete, rimedio) intercettata dal try/catch di `Program.cs` → `Log.Fatal` +
  **`Environment.ExitCode = 1`** (senza, il fail-fast mentirebbe a orchestratore/CI).
- **Solo segreti veri nel vault**: `ConnectionStrings--Default`; in compose anche
  `ServiceBus--ConnectionString` (l'emulatore ASB vuole SAS). In Azure la SB connection string
  NON esiste (managed identity + FQNS). AzureAd ClientId/TenantId/Audience NON sono segreti.
- **Naming secret**: `--` → `:` (`ConnectionStrings--Default` → `ConnectionStrings:Default`).

## Emulatore (compose + test) — posture hardened

- Immagine `jamesgoulddev/azure-keyvault-emulator` **pinnata 3.1.0** (`3.1.0-arm` per arm64;
  in compose via `KEYVAULT_EMULATOR_TAG`/`_PLATFORM` in `.env`).
- Contratto dell'immagine: HTTPS su **4997**, cert `/certs/emulator.pfx` password `emulator`.
- **NIENTE NuGet del progetto emulatore** (il loro modulo scrive una CA nel trust store host):
  Testcontainers liscio + cert generato per-run (`CertificateRequest` nei test, openssl in
  compose) + trust del self-signed scoped al solo SecretClient + **JWT mintato localmente**
  (l'emulatore non valida firma/claim → niente round-trip token).
- In compose: `keyvault-certs` (one-shot, idempotente) → `keyvault` (healthcheck TCP bash
  /dev/tcp) → `keyvault-seed` (one-shot, REST PUT con retry) → `api` (`KeyVault__Uri=https://keyvault:4997`,
  `KeyVault__Credential=Emulator`). L'env dell'api NON contiene più connection string.

## Config quick reference

```json
{ "KeyVault": { "Uri": "", "Credential": "ManagedIdentity", "ManagedIdentityClientId": "", "ReloadInterval": "" } }
```

`ReloadInterval` (TimeSpan, es. `00:05:00`) = rilettura periodica per rotazione senza restart;
assente/vuoto = solo allo startup.

## Test: pattern da riusare

- **Config letta in fase builder** (come `KeyVault:Uri`): nei test WAF va iniettata con
  `builder.UseSetting(...)`, NON `ConfigureAppConfiguration` (invisibile lì) — [L25].
- `KeyVaultEnabledApiFactory` disattiva il ripunto del DbContext
  (`OverrideDbContextWithTestContainer => false`) e seeda la connection string SOLO nel vault →
  l'e2e prova il provider per costruzione.
- Fail-fast senza Docker: `WebApplication.CreateBuilder` + `KeyVault:Uri` su porta chiusa →
  `Assert.Throws` sul messaggio parlante.
