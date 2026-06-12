using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace WebApiPlayground.Api.Configuration.KeyVault;

/// <summary>
/// Aggancia Azure Key Vault come <b>configuration provider</b> (composition root = Api). Config-gated:
/// con <c>KeyVault:Uri</c> vuoto è un no-op. Va chiamato PRIMA di <c>StartupConfigurationValidator</c>,
/// così i segreti del vault (es. <c>ConnectionStrings--Default</c>) soddisfano il fail-fast. Essendo
/// l'ULTIMO provider aggiunto, i secret del vault vincono su appsettings/env var (precedenza standard
/// di IConfiguration). Ogni fallimento di bootstrap → eccezione PARLANTE (uri, credential, cause probabili,
/// rimedio): la intercetta il try/catch di <c>Program.cs</c> → <c>Log.Fatal</c> e processo che esce non-zero.
/// Vedi <c>docs/keyvault.md</c> e <c>.claude/context/keyvault.md</c>.
/// </summary>
public static class KeyVaultConfigurationExtensions
{
    /// <summary>Legge la sezione <c>KeyVault</c> e, se <c>Uri</c> è valorizzato, carica i secret nel config.</summary>
    public static void AddKeyVaultIfConfigured(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(KeyVaultOptions.SectionName).Get<KeyVaultOptions>()
                      ?? new KeyVaultOptions();

        if (string.IsNullOrWhiteSpace(options.Uri))
            return; // Provider spento: i segreti arrivano da appsettings/env var (o KV references della piattaforma).

        if (!Uri.TryCreate(options.Uri, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"KeyVault:Uri (env: KeyVault__Uri) non è un URI https assoluto: '{options.Uri}'. " +
                "Atteso es. 'https://kv-webapiplay-dev-xxxxxx.vault.azure.net/' (output 'keyVaultUri' del deploy Bicep). " +
                "Per spegnere il provider lasciare la chiave vuota. Vedi docs/keyvault.md.");

        var credential = CreateCredential(options, builder.Environment);

        var clientOptions = new SecretClientOptions();
        if (IsEmulator(options))
        {
            // L'SDK rifiuta vault fuori da *.vault.azure.net (challenge resource verification) e pretende TLS
            // valido: l'emulatore gira su hostname arbitrario con certificato self-signed. Entrambi i bypass
            // sono ACCETTABILI SOLO QUI perché Emulator è consentito esclusivamente in Development (guard in
            // CreateCredential) e l'emulatore non contiene mai segreti reali. Vedi docs/keyvault.md.
            clientOptions.DisableChallengeResourceVerification = true;
            clientOptions.Transport = new HttpClientTransport(new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }));
        }

        var client = new SecretClient(vaultUri, credential, clientOptions);

        try
        {
            // ConfigurationManager carica il provider SUBITO: un vault irraggiungibile/negato fallisce qui,
            // non alla prima lettura di un secret — il fail-fast resta tutto allo startup.
            builder.Configuration.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions
            {
                ReloadInterval = options.ReloadInterval,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(BuildLoadFailureMessage(options, vaultUri, ex), ex);
        }
    }

    /// <summary>
    /// Credential ESPLICITA per ambiente (niente <c>DefaultAzureCredential</c>: catena non deterministica,
    /// startup più lenta ed errori opachi — guidance Azure SDK). Valore non riconosciuto o
    /// <c>Emulator</c> fuori da Development → errore parlante.
    /// </summary>
    public static TokenCredential CreateCredential(KeyVaultOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (string.Equals(options.Credential, KeyVaultCredentialTypes.ManagedIdentity, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));

        if (string.Equals(options.Credential, KeyVaultCredentialTypes.AzureCli, StringComparison.OrdinalIgnoreCase))
            return new AzureCliCredential();

        if (string.Equals(options.Credential, KeyVaultCredentialTypes.Emulator, StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    $"KeyVault:Credential = 'Emulator' non è consentito nell'ambiente '{environment.EnvironmentName}': " +
                    "l'emulatore Key Vault è solo per sviluppo locale/test (TLS self-signed accettato, token non verificati). " +
                    "Fuori da Development usare 'ManagedIdentity' (Azure) o 'AzureCli' (run locale contro il vault reale). " +
                    "Vedi docs/keyvault.md.");

            return new KeyVaultEmulatorCredential();
        }

        throw new InvalidOperationException(
            $"KeyVault:Credential (env: KeyVault__Credential) = '{options.Credential}' non riconosciuto. " +
            $"Valori ammessi: {string.Join(", ", KeyVaultCredentialTypes.All)}. Vedi docs/keyvault.md.");
    }

    /// <summary>
    /// Messaggio parlante per il fallimento di caricamento dei secret: dice DOVE stava puntando l'app,
    /// CON quale credential, le cause probabili per il tipo di errore e il rimedio. È il motivo per cui
    /// l'app non parte: deve bastare da solo a diagnosticare (requisito del fail-fast, come [L23]).
    /// </summary>
    public static string BuildLoadFailureMessage(KeyVaultOptions options, Uri vaultUri, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(vaultUri);
        ArgumentNullException.ThrowIfNull(ex);

        var causes = ProbableCauses(options, ex);

        return
            $"Impossibile caricare i secret da Azure Key Vault '{vaultUri}' (KeyVault:Credential = '{options.Credential}'). " +
            $"Errore: {ex.GetBaseException().Message}{Environment.NewLine}" +
            $"Cause probabili:{Environment.NewLine}" +
            string.Join(Environment.NewLine, causes.Select(c => $"  - {c}")) + Environment.NewLine +
            "Per avviare senza Key Vault: svuotare KeyVault:Uri (env: KeyVault__Uri) e fornire i segreti via env var. " +
            "Vedi docs/keyvault.md (sezione Troubleshooting).";
    }

    private static bool IsEmulator(KeyVaultOptions options) =>
        string.Equals(options.Credential, KeyVaultCredentialTypes.Emulator, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ProbableCauses(KeyVaultOptions options, Exception ex)
    {
        // Il provider può consegnare l'errore originale avvolto (es. AggregateException): si
        // diagnostica sulla causa radice.
        var root = ex.GetBaseException();

        // 403 = autenticato ma non autorizzato: problema di RBAC, non di credential.
        if (root is Azure.RequestFailedException { Status: 403 })
            return
            [
                "l'identity è autenticata ma NON ha il ruolo RBAC 'Key Vault Secrets User' sul vault",
                "rimedio: passare l'object id come 'appPrincipalId' al deploy Bicep (role assignment già nel modulo keyvault.bicep), oppure 'az role assignment create --role \"Key Vault Secrets User\" --assignee <objectId> --scope <vault resource id>'",
            ];

        if (root is Azure.RequestFailedException { Status: 401 })
            return
            [
                "il token presentato non è valido per questo vault (tenant/audience errati)",
                "verificare che l'identity appartenga allo stesso tenant del vault",
            ];

        if (root is AuthenticationFailedException or CredentialUnavailableException)
            return string.Equals(options.Credential, KeyVaultCredentialTypes.AzureCli, StringComparison.OrdinalIgnoreCase)
                ?
                [
                    "Azure CLI non loggata o sessione scaduta: eseguire 'az login' (ed eventualmente 'az account set --subscription <id>')",
                ]
                : (IReadOnlyList<string>)
                [
                    "la managed identity non è disponibile: l'app non sta girando su una risorsa Azure con identity assegnata (in locale non esiste IMDS)",
                    "se user-assigned: verificare KeyVault:ManagedIdentityClientId (env: KeyVault__ManagedIdentityClientId)",
                    "per il run locale contro il vault reale usare KeyVault:Credential = 'AzureCli' (richiede 'az login')",
                ];

        // Errori di rete/timeout: il vault non risponde affatto.
        return
        [
            "URI del vault errato (vault inesistente o nome sbagliato)",
            "firewall del vault default-deny: il tuo IP non è negli 'allowedIpAddresses' del deploy Bicep (infra/main.*.bicepparam)",
            "rete/DNS: vault raggiungibile solo da private endpoint, o nessuna connettività verso *.vault.azure.net",
            "se emulatore: container non avviato o hostname/porta errati (atteso es. https://keyvault:4997 in docker compose)",
        ];
    }
}
