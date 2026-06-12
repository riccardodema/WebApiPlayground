namespace WebApiPlayground.Api.Configuration.KeyVault;

/// <summary>
/// Opzioni del config provider Azure Key Vault (sezione <c>KeyVault</c>). Config-gated come
/// Cache/OpenTelemetry/ServiceBus: con <see cref="Uri"/> vuoto il provider è spento e i segreti arrivano
/// da appsettings/env var; valorizzandolo i secret del vault entrano in <c>IConfiguration</c> all'avvio,
/// PRIMA del fail-fast di <c>StartupConfigurationValidator</c> (nome secret: <c>--</c> → <c>:</c>, es.
/// <c>ConnectionStrings--Default</c> → <c>ConnectionStrings:Default</c>) e VINCONO su appsettings/env var.
/// Vedi <c>docs/keyvault.md</c> e <c>.claude/context/keyvault.md</c>.
/// </summary>
public sealed class KeyVaultOptions
{
    public const string SectionName = "KeyVault";

    /// <summary>URI del vault (es. <c>https://kv-webapiplay-dev-xxxxxx.vault.azure.net/</c>). Vuoto ⇒ provider spento.</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Credential esplicita (niente <c>DefaultAzureCredential</c>: deterministica per ambiente, errori parlanti).
    /// Valori ammessi in <see cref="KeyVaultCredentialTypes"/>: <c>ManagedIdentity</c> (default, Azure),
    /// <c>AzureCli</c> (run locale contro il vault reale, richiede <c>az login</c>), <c>Emulator</c>
    /// (emulatore in container, SOLO Development).
    /// </summary>
    public string Credential { get; set; } = KeyVaultCredentialTypes.ManagedIdentity;

    /// <summary>ClientId della user-assigned managed identity. Vuoto = system-assigned.</summary>
    public string ManagedIdentityClientId { get; set; } = string.Empty;

    /// <summary>
    /// Intervallo di ricarica dei secret dal vault (rotazione senza restart, es. <c>00:05:00</c>).
    /// Null (default) = i secret si leggono solo all'avvio.
    /// </summary>
    public TimeSpan? ReloadInterval { get; set; }
}

/// <summary>Valori ammessi per <see cref="KeyVaultOptions.Credential"/>.</summary>
public static class KeyVaultCredentialTypes
{
    public const string ManagedIdentity = "ManagedIdentity";
    public const string AzureCli = "AzureCli";
    public const string Emulator = "Emulator";

    public static readonly string[] All = [ManagedIdentity, AzureCli, Emulator];
}
