using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Api.Configuration.KeyVault;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.KeyVault;

/// <summary>
/// Factory che attiva il <b>Key Vault config provider reale</b> contro l'emulatore in container.
/// La prova è strutturale: il ripunto del DbContext sul container SQL è DISATTIVATO e la connection
/// string del DB viene seedata SOLO nel vault → se l'app si avvia e raggiunge il database, la
/// configurazione è passata necessariamente dal provider Key Vault (boot reale: <c>KeyVault:Uri</c> +
/// <c>Credential=Emulator</c>, gli stessi dell'utente in docker compose). Usata solo da
/// <see cref="KeyVaultConfigProviderTests"/>.
/// </summary>
public sealed class KeyVaultEnabledApiFactory : PlaygroundApiFactory, IAsyncLifetime
{
    public const string SmokeSecretValue = "from-vault";
    public const string QueueNameFromVault = "queue-from-vault";

    private readonly KeyVaultEmulatorContainer _keyVault = new();

    // La connection string NON viene iniettata nei servizi: deve arrivare dal vault.
    protected override bool OverrideDbContextWithTestContainer => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Seed PRIMA che l'host si costruisca (ConfigureWebHost gira alla prima richiesta di Services,
        // a container SQL già avviato): nomi con '--' → sezioni di configurazione.
        var secrets = _keyVault.CreateSecretClient();
        secrets.SetSecret("ConnectionStrings--Default", SqlConnectionString);
        secrets.SetSecret("KeyVaultSmoke--Value", SmokeSecretValue);
        // Chiave che ESISTE già in appsettings.json: serve a provare che il vault vince (provider aggiunto
        // per ultimo). Innocua: senza ServiceBus configurato il trasporto resta in-process e non la usa.
        secrets.SetSecret("ServiceBus--QueueName", QueueNameFromVault);

        // UseSetting (host configuration), NON ConfigureAppConfiguration: KeyVault:Uri viene letto da
        // Program.cs in FASE BUILDER (AddKeyVaultIfConfigured), e i provider aggiunti via
        // ConfigureAppConfiguration dalla factory arrivano solo DOPO — sarebbero invisibili lì.
        builder.UseSetting("KeyVault:Uri", _keyVault.GetEndpoint());
        builder.UseSetting("KeyVault:Credential", KeyVaultCredentialTypes.Emulator);

        base.ConfigureWebHost(builder);
    }

    // L'emulatore va avviato PRIMA che la base costruisca l'host (ConfigureWebHost fa il seed).
    async Task IAsyncLifetime.InitializeAsync()
    {
        await _keyVault.StartAsync();
        await base.InitializeAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _keyVault.DisposeAsync();
    }
}

/// <summary>
/// End-to-end del <b>Key Vault config provider</b> contro l'emulatore (Testcontainers, nessun account
/// Azure): l'app parte con i segreti SOLO nel vault, i nomi <c>--</c> diventano sezioni, e i valori del
/// vault vincono su appsettings. È la stessa meccanica del vault reale (cambiano solo URI e credential)
/// → quando si collega la subscription si riconfigura, non si ri-verifica. Vedi <c>docs/keyvault.md</c>.
/// </summary>
[Collection("Integration")]
public class KeyVaultConfigProviderTests : IClassFixture<KeyVaultEnabledApiFactory>
{
    private readonly KeyVaultEnabledApiFactory _factory;

    public KeyVaultConfigProviderTests(KeyVaultEnabledApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task App_boots_and_reaches_the_database_with_connection_string_only_in_the_vault()
    {
        // Il DbContext non è ripuntato dai test e la connection string non è in env/appsettings:
        // un 200 dal DB prova che il valore è arrivato dal vault attraverso il provider.
        var client = _factory.CreateClientWithScope(BooksPermissions.ScopeRead);

        var response = await client.GetAsync("/api/v1/books");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Secret_names_with_double_dash_map_to_configuration_sections()
    {
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();

        // 'KeyVaultSmoke--Value' nel vault → 'KeyVaultSmoke:Value' in IConfiguration.
        Assert.Equal(KeyVaultEnabledApiFactory.SmokeSecretValue, configuration["KeyVaultSmoke:Value"]);
    }

    [Fact]
    public void Vault_secrets_win_over_appsettings_values()
    {
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();

        // appsettings.json dice 'popularity-enrichment': il provider Key Vault è aggiunto per ULTIMO
        // (precedenza standard di IConfiguration) quindi il valore del vault deve vincere.
        Assert.Equal(KeyVaultEnabledApiFactory.QueueNameFromVault, configuration["ServiceBus:QueueName"]);
    }
}

/// <summary>
/// Fail-fast del provider SENZA Docker: vault configurato ma irraggiungibile (porta chiusa) → il
/// bootstrap deve fallire SUBITO (niente app "mezza su") con il messaggio parlante che spiega dove
/// puntava, con quale credential, le cause probabili e come avviare senza Key Vault.
/// </summary>
public class KeyVaultStartupFailureTests
{
    [Fact]
    public void Unreachable_vault_fails_startup_with_talking_message()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Uri"] = "https://127.0.0.1:1", // nessun listener: connessione rifiutata
            ["KeyVault:Credential"] = KeyVaultCredentialTypes.Emulator,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddKeyVaultIfConfigured());

        Assert.Contains("Impossibile caricare i secret", ex.Message);
        Assert.Contains("https://127.0.0.1:1", ex.Message);     // DOVE puntava
        Assert.Contains("Emulator", ex.Message);                 // con quale credential
        Assert.Contains("Cause probabili", ex.Message);          // diagnosi
        Assert.Contains("KeyVault__Uri", ex.Message);            // rimedio (come spegnere il provider)
    }
}
