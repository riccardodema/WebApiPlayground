namespace WebApiPlayground.Api.Configuration;

/// <summary>
/// Fail-fast esplicito sulla configurazione obbligatoria. <b>Fuori da Development</b> (es. l'immagine
/// container in Production) l'app DEVE rifiutarsi di avviarsi se manca un settaggio senza cui non può
/// funzionare — elencandoli <b>tutti in un solo messaggio parlante</b>, con anche la forma <i>env var</i>,
/// così chi esegue sa esattamente cosa impostare (e può aggiungerlo). In <b>Development</b> restano
/// opzionali: connection string da <c>appsettings.Development.json</c> e autenticazione in BYPASS di
/// sviluppo (vedi <c>.claude/context/auth.md</c>).
///
/// <para>Stesso gate (<c>!IsDevelopment</c>) del bypass auth in <c>AuthenticationExtensions</c>: i test e
/// docker-compose girano in Development e non sono toccati. Vedi <c>.claude/context/docker.md</c> e
/// <c>.claude/lessons.md</c> [L23].</para>
/// </summary>
public static class StartupConfigurationValidator
{
    /// <summary>Chiavi obbligatorie fuori da Development, con il motivo mostrato se mancano.</summary>
    private static readonly (string Key, string Why)[] RequiredOutsideDevelopment =
    [
        ("ConnectionStrings:Default", "connessione al database SQL Server"),
        ("AzureAd:ClientId", "autenticazione Microsoft Entra ID"),
        ("AzureAd:TenantId", "autenticazione Microsoft Entra ID"),
        ("AzureAd:Audience", "autenticazione Microsoft Entra ID"),
        // Trasporto dell'outbox: in Production il broker è il percorso reale (no fallback in-process). Si richiede
        // il namespace (managed identity, no SAS) — non la ConnectionString, che è solo per emulatore/locale.
        ("ServiceBus:FullyQualifiedNamespace", "trasporto outbox su Azure Service Bus (managed identity)"),
    ];

    /// <summary>
    /// Verifica le chiavi obbligatorie. In Development è un no-op; altrimenti, se ne manca anche una sola,
    /// lancia <see cref="InvalidOperationException"/> con l'<b>elenco completo</b> di quelle mancanti
    /// (chiave + env var + perché serve). L'eccezione è intercettata dal <c>try/catch</c> di
    /// <c>Program.cs</c> che fa <c>Log.Fatal</c> → il processo esce non-zero col messaggio in chiaro.
    /// </summary>
    public static void ValidateRequiredConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment())
            return;

        var missing = RequiredOutsideDevelopment
            .Where(entry => string.IsNullOrWhiteSpace(configuration[entry.Key]))
            .ToArray();

        if (missing.Length == 0)
            return;

        throw new InvalidOperationException(BuildMessage(environment.EnvironmentName, missing));
    }

    private static string BuildMessage(string environmentName, IReadOnlyList<(string Key, string Why)> missing)
    {
        var lines = missing.Select(entry => $"  - {entry.Key,-26} (env: {ToEnvVar(entry.Key)})  → {entry.Why}");
        return
            $"Configurazione obbligatoria mancante per l'ambiente '{environmentName}'. " +
            $"Impostare le seguenti chiavi prima di avviare l'app:{Environment.NewLine}" +
            string.Join(Environment.NewLine, lines) + Environment.NewLine +
            "In Development (ASPNETCORE_ENVIRONMENT=Development) sono opzionali: connection string da " +
            "appsettings.Development.json e autenticazione in BYPASS di sviluppo.";
    }

    /// <summary>Forma <i>environment variable</i> di una chiave di config: separatore <c>:</c> → <c>__</c>.</summary>
    private static string ToEnvVar(string key) => key.Replace(":", "__");
}
