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

        // Il messaggio nomina la chiave (anche in forma env var), il valore errato e come spegnere il
        // provider. L'assert sul testo SPECIFICO distingue il fail-fast di validazione URI da un
        // fallimento di caricamento (che contiene comunque la parola 'https').
        Assert.Contains("KeyVault:Uri", ex.Message);
        Assert.Contains("KeyVault__Uri", ex.Message);
        Assert.Contains("http://insecure-vault.example", ex.Message);
        Assert.Contains("non è un URI https assoluto", ex.Message);
    }

    [Fact]
    public void Null_builder_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => KeyVaultConfigurationExtensions.AddKeyVaultIfConfigured(null!));
    }

    [Fact]
    public void Unreachable_emulator_vault_fails_through_the_emulator_transport_branch()
    {
        // Percorre il ramo Emulator REALE (challenge verification off + transport che accetta il TLS
        // self-signed) fino al fallimento di caricamento: porta chiusa su loopback, deterministico.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Uri"] = "https://127.0.0.1:1",
            ["KeyVault:Credential"] = KeyVaultCredentialTypes.Emulator,
        });

        var ex = Assert.Throws<InvalidOperationException>(builder.AddKeyVaultIfConfigured);

        Assert.Contains("Impossibile caricare i secret", ex.Message);
        Assert.Contains("KeyVault:Credential = 'Emulator'", ex.Message);
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

        // La causa è SPECIFICA del ramo AzureCli ('az login' compare anche nei suggerimenti del
        // ramo managed identity: da solo non distinguerebbe i due).
        Assert.Contains("sessione scaduta", message);
        Assert.DoesNotContain("IMDS", message);
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

    // ---- I messaggi parlanti SONO il contratto del fail-fast: si asseriscono per esteso ---------
    // (i mutanti sulle stringhe diagnostiche devono morire: un hint sbagliato = utente bloccato).

    [Fact]
    public void Relative_uri_fails_with_the_same_talking_message_as_non_https()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Uri"] = "not-an-absolute-uri",
        });

        var ex = Assert.Throws<InvalidOperationException>(builder.AddKeyVaultIfConfigured);

        Assert.Contains("non è un URI https assoluto", ex.Message);
        Assert.Contains("keyVaultUri", ex.Message);          // rimanda all'output del deploy Bicep
        Assert.Contains("Per spegnere il provider", ex.Message);
    }

    [Fact]
    public void Credential_matching_is_case_insensitive_for_managed_identity_too()
    {
        var credential = KeyVaultConfigurationExtensions.CreateCredential(
            new KeyVaultOptions { Credential = "MANAGEDIDENTITY" }, Env(Environments.Production));

        Assert.IsType<ManagedIdentityCredential>(credential);
    }

    [Fact]
    public void Emulator_guard_message_explains_why_and_points_to_the_docs()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KeyVaultConfigurationExtensions.CreateCredential(
            new KeyVaultOptions { Credential = KeyVaultCredentialTypes.Emulator }, Env(Environments.Production)));

        Assert.Contains("solo per sviluppo locale/test", ex.Message); // il PERCHÉ del divieto
        Assert.Contains("docs/keyvault.md", ex.Message);
    }

    [Fact]
    public void Unknown_credential_message_names_the_env_var_form()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KeyVaultConfigurationExtensions.CreateCredential(
            new KeyVaultOptions { Credential = "Nope" }, Env(Environments.Production)));

        Assert.Contains("KeyVault__Credential", ex.Message);
        Assert.Contains("Valori ammessi", ex.Message);
    }

    [Fact]
    public void Load_failure_message_names_vault_credential_and_root_error()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new HttpRequestException("connessione rifiutata"));

        Assert.Contains("Impossibile caricare i secret da Azure Key Vault", message);
        Assert.Contains("KeyVault:Credential = 'ManagedIdentity'", message); // CON quale credential
        Assert.Contains("Errore: connessione rifiutata", message);            // la causa radice originale
        Assert.Contains("Cause probabili:", message);
    }

    [Fact]
    public void Unauthorized_401_points_to_tenant_mismatch()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new RequestFailedException(401, "Unauthorized"));

        Assert.Contains("token presentato non è valido", message);
        Assert.Contains("stesso tenant", message);
    }

    [Fact]
    public void Forbidden_403_includes_the_manual_role_assignment_remedy()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new RequestFailedException(403, "Forbidden"));

        Assert.Contains("az role assignment create", message);
    }

    [Fact]
    public void Managed_identity_causes_include_the_user_assigned_hint()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new CredentialUnavailableException("no IMDS"));

        Assert.Contains("IMDS", message);
        Assert.Contains("KeyVault__ManagedIdentityClientId", message);   // hint per la user-assigned
        Assert.Contains("KeyVault:Credential = 'AzureCli'", message.Replace("'AzureCli'", "'AzureCli'")); // alternativa locale
        Assert.Contains("az login", message);
    }

    [Fact]
    public void Network_causes_list_every_actionable_hypothesis()
    {
        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, new HttpRequestException("timeout"));

        Assert.Contains("URI del vault errato", message);
        Assert.Contains("allowedIpAddresses", message);
        Assert.Contains("private endpoint", message);
        Assert.Contains("https://keyvault:4997", message); // l'ipotesi emulatore/compose
    }

    [Fact]
    public void Wrapped_exceptions_are_diagnosed_on_their_root_cause()
    {
        // Il provider può consegnare l'errore avvolto (AggregateException): la diagnosi non deve cambiare.
        var wrapped = new AggregateException(new RequestFailedException(403, "Forbidden"));

        var message = KeyVaultConfigurationExtensions.BuildLoadFailureMessage(
            new KeyVaultOptions(), VaultUri, wrapped);

        Assert.Contains("Key Vault Secrets User", message);
    }

    [Fact]
    public void Public_entry_points_reject_null_arguments()
    {
        var options = new KeyVaultOptions();
        var env = Env(Environments.Production);

        Assert.Throws<ArgumentNullException>(() => KeyVaultConfigurationExtensions.CreateCredential(null!, env));
        Assert.Throws<ArgumentNullException>(() => KeyVaultConfigurationExtensions.CreateCredential(options, null!));
        Assert.Throws<ArgumentNullException>(() => KeyVaultConfigurationExtensions.BuildLoadFailureMessage(null!, VaultUri, new Exception()));
        Assert.Throws<ArgumentNullException>(() => KeyVaultConfigurationExtensions.BuildLoadFailureMessage(options, null!, new Exception()));
        Assert.Throws<ArgumentNullException>(() => KeyVaultConfigurationExtensions.BuildLoadFailureMessage(options, VaultUri, null!));
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
