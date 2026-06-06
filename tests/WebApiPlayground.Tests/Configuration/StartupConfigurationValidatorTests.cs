using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WebApiPlayground.Api.Configuration;
using Xunit;

namespace WebApiPlayground.Tests.Configuration;

/// <summary>
/// Il fail-fast di startup: fuori da Development l'app deve rifiutarsi di partire se manca
/// configurazione obbligatoria, elencando <b>tutte</b> le chiavi mancanti (con la forma env var).
/// In Development invece è tollerante (BYPASS auth + connection string locale).
/// Vedi <see cref="StartupConfigurationValidator"/> e .claude/context/docker.md.
/// </summary>
public class StartupConfigurationValidatorTests
{
    private static readonly string[] AllRequiredKeys =
    [
        "ConnectionStrings:Default",
        "AzureAd:ClientId",
        "AzureAd:TenantId",
        "AzureAd:Audience",
    ];

    [Fact]
    public void Production_with_all_keys_missing_throws_listing_every_key_and_its_env_var()
    {
        var configuration = BuildConfiguration(); // niente impostato

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupConfigurationValidator.ValidateRequiredConfiguration(configuration, Env(Environments.Production)));

        // L'ambiente è nominato e tutte le chiavi mancanti sono elencate...
        Assert.Contains("Production", ex.Message);
        foreach (var key in AllRequiredKeys)
            Assert.Contains(key, ex.Message);

        // ...con la loro forma env var (':' → '__'), così l'utente sa cosa esportare.
        Assert.Contains("ConnectionStrings__Default", ex.Message);
        Assert.Contains("AzureAd__ClientId", ex.Message);
        Assert.Contains("AzureAd__TenantId", ex.Message);
        Assert.Contains("AzureAd__Audience", ex.Message);
    }

    [Fact]
    public void Production_with_all_keys_present_does_not_throw()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=db;Database=Playground;User ID=sa;Password=x;TrustServerCertificate=True;",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:Audience"] = "api://playground",
        });

        var exception = Record.Exception(
            () => StartupConfigurationValidator.ValidateRequiredConfiguration(configuration, Env(Environments.Production)));

        Assert.Null(exception);
    }

    [Fact]
    public void Development_with_all_keys_missing_does_not_throw()
    {
        var configuration = BuildConfiguration(); // niente impostato

        var exception = Record.Exception(
            () => StartupConfigurationValidator.ValidateRequiredConfiguration(configuration, Env(Environments.Development)));

        Assert.Null(exception);
    }

    [Fact]
    public void Production_lists_only_the_keys_that_are_actually_missing()
    {
        // Connection string presente: deve mancare SOLO il blocco AzureAd nel messaggio.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=db;Database=Playground;User ID=sa;Password=x;TrustServerCertificate=True;",
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => StartupConfigurationValidator.ValidateRequiredConfiguration(configuration, Env(Environments.Production)));

        Assert.DoesNotContain("ConnectionStrings:Default", ex.Message);
        Assert.Contains("AzureAd:ClientId", ex.Message);
        Assert.Contains("AzureAd:TenantId", ex.Message);
        Assert.Contains("AzureAd:Audience", ex.Message);
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?>? values = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();

    private static IHostEnvironment Env(string environmentName) => new FakeHostEnvironment
    {
        EnvironmentName = environmentName,
    };

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "WebApiPlayground.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
