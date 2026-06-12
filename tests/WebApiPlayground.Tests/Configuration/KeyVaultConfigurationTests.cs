using Azure;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WebApiPlayground.Api.Configuration.KeyVault;
using Xunit;

namespace WebApiPlayground.Tests.Configuration;

/// <summary>
/// Il bootstrap del Key Vault config provider: config-gating su <c>KeyVault:Uri</c>, scelta ESPLICITA
/// della credential per ambiente (mai <c>DefaultAzureCredential</c>), guard dell'emulatore fuori da
/// Development e messaggi di fallimento PARLANTI (il motivo per cui l'app non parte deve bastare da solo
/// a diagnosticare). Vedi <see cref="KeyVaultConfigurationExtensions"/> e docs/keyvault.md.
/// </summary>
public class KeyVaultConfigurationTests
{
    // ---- Config-gating -------------------------------------------------------

    [Fact]
    public void Empty_uri_is_a_no_op_and_adds_no_configuration_source()
    {
        var builder = WebApplication.CreateBuilder();
        var sourcesBefore = builder.Configuration.Sources.Count;

        builder.AddKeyVaultIfConfigured();

        Assert.Equal(sourcesBefore, builder.Configuration.Sources.Count);
    }

    [Fact]
    public void Non_https_uri_fails_fast_with_talking_message()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Uri"] = "http://insecure-vault.example",
        });

        var ex = Assert.Throws<InvalidOperationException>(builder.AddKeyVaultIfConfigured);

        // Il messaggio nomina la chiave (anche in forma env var), il valore errato e come spegnere il provider.
        Assert.Contains("KeyVault:Uri", ex.Message);
        Assert.Contains("KeyVault__Uri", ex.Message);
        Assert.Contains("http://insecure-vault.example", ex.Message);
        Assert.Contains("https", ex.Message);
    }

    // ---- Credential esplicita per ambiente ------------------------------------

    [Fact]
    public void ManagedIdentity_is_the_default_credential()
    {
        var credential = KeyVaultConfigurationExtensions.CreateCredential(new KeyVaultOptions(), Env(Environments.Production));

        Assert.IsType<ManagedIdentityCredential>(credential);
    }

    [Fact]
    public void ManagedIdentity_with_client_id_builds_user_assigned_credential()
    {
        var options = new KeyVaultOptions { ManagedIdentityClientId = "00000000-0000-0000-0000-000000000001" };

        var credential = KeyVaultConfigurationExtensions.CreateCredential(options, Env(Environments.Production));

        Assert.IsType<ManagedIdentityCredential>(credential);
    }

    [Fact]
    public void AzureCli_credential_is_supported_for_local_runs_against_the_real_vault()
    {
        var options = new KeyVaultOptions { Credential = KeyVaultCredentialTypes.AzureCli };

        var credential = KeyVaultConfigurationExtensions.CreateCredential(options, Env(Environments.Production));

        Assert.IsType<AzureCliCredential>(credential);
    }

    [Fact]
    public void Credential_values_are_case_insensitive()
    {
        var options = new KeyVaultOptions { Credential = "azurecli" };

        var credential = KeyVaultConfigurationExtensions.CreateCredential(options, Env(Environments.Production));

        Assert.IsType<AzureCliCredential>(credential);
    }

    [Fact]
    public void Unknown_credential_value_fails_listing_the_allowed_ones()
    {
        var options = new KeyVaultOptions { Credential = "VisualStudio" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => KeyVaultConfigurationExtensions.CreateCredential(options, Env(Environments.Production)));

        Assert.Contains("VisualStudio", ex.Message);
        foreach (var allowed in KeyVaultCredentialTypes.All)
            Assert.Contains(allowed, ex.Message);
    }

    // ---- Emulatore: solo Development ------------------------------------------

    [Fact]
    public void Emulator_credential_in_Development_mints_a_well_formed_jwt_locally()
    {
        var options = new KeyVaultOptions { Credential = KeyVaultCredentialTypes.Emulator };

        var credential = KeyVaultConfigurationExtensions.CreateCredential(options, Env(Environments.Development));
        var token = credential.GetToken(new TokenRequestContext(["https://vault.azure.net/.default"]), default);

        // JWT ben formato (header.payload.signature): l'emulatore non valida firma/claim, gli basta il parsing.
        Assert.Equal(3, token.Token.Split('.').Length);
        Assert.True(token.ExpiresOn > DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Emulator_credential_outside_Development_fails_fast_with_talking_message(string environment)
    {
        var options = new KeyVaultOptions { Credential = KeyVaultCredentialTypes.Emulator };

        var ex = Assert.Throws<InvalidOperationException>(
            () => KeyVaultConfigurationExtensions.CreateCredential(options, Env(environment)));

        // Nomina l'ambiente, spiega il perché del divieto e indica le alternative corrette.
        Assert.Contains(environment, ex.Message);
        Assert.Contains("Emulator", ex.Message);
        Assert.Contains(KeyVaultCredentialTypes.ManagedIdentity, ex.Message);
        Assert.Contains(KeyVaultCredentialTypes.AzureCli, ex.Message);
    }

    // ---- Messaggi di fallimento parlanti ---------------------------------------

    private static readonly Uri VaultUri = new("https://kv-webapiplay-dev-abc123.vault.azure.net/");

    [Fact]
    public void Forbidden_403_points_to_the_missing_rbac_role()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new RequestFailedException(403, "Forbidden"));

        Assert.Contains(VaultUri.ToString(), message);
        Assert.Contains("Key Vault Secrets User", message);
        Assert.Contains("appPrincipalId", message); // il rimedio rimanda al parametro del deploy Bicep
    }

    [Fact]
    public void Managed_identity_unavailable_explains_imds_and_suggests_AzureCli_for_local_runs()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new CredentialUnavailableException("No managed identity endpoint"));

        Assert.Contains("managed identity", message);
        Assert.Contains("AzureCli", message);
    }

    [Fact]
    public void AzureCli_auth_failure_points_to_az_login()
    {
        var options = new KeyVaultOptions { Credential = KeyVaultCredentialTypes.AzureCli };

        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            options, VaultUri, new AuthenticationFailedException("Please run 'az login'"));

        Assert.Contains("az login", message);
    }

    [Fact]
    public void Network_failure_points_to_firewall_and_wrong_uri()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new HttpRequestException("Connection refused"));

        Assert.Contains("firewall", message);
        Assert.Contains("allowedIpAddresses", message);
    }

    [Fact]
    public void Failure_message_always_says_how_to_start_without_key_vault()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new HttpRequestException("any"));

        Assert.Contains("KeyVault__Uri", message);
        Assert.Contains("docs/keyvault.md", message);
    }

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
