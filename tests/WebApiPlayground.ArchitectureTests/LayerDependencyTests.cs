using NetArchTest.Rules;
using Xunit;

namespace WebApiPlayground.ArchitectureTests;

/// <summary>
/// Fa rispettare le regole di dipendenza tra layer della Clean Architecture
/// (vedi <c>.claude/context/architecture.md</c>). NetArchTest ispeziona l'IL: un
/// riferimento "vietato" introdotto per errore (project reference + using) fa
/// fallire il build della CI, così l'architettura non può divergere in silenzio.
///
///   API  →  Application  →  Domain
///    ↓            ↓
///   Infrastructure
/// </summary>
public class LayerDependencyTests
{
    // ---- Domain: nessuna dipendenza in uscita -------------------------------

    [Fact]
    public void Domain_should_not_depend_on_any_other_layer()
    {
        var result = Types.InAssembly(ArchitectureRules.DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                [
                    ArchitectureRules.ApplicationNamespace,
                    ArchitectureRules.InfrastructureNamespace,
                    .. ArchitectureRules.ApiNamespaces,
                ])
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Domain_should_not_depend_on_EntityFrameworkCore()
    {
        // Le entità sono POCO: la persistenza vive in Infrastructure.
        var result = Types.InAssembly(ArchitectureRules.DomainAssembly)
            .ShouldNot()
            .HaveDependencyOn(ArchitectureRules.EntityFrameworkNamespace)
            .GetResult();

        AssertArchitecture(result);
    }

    // ---- Application: solo Domain -------------------------------------------

    [Fact]
    public void Application_should_not_depend_on_Infrastructure_or_Api()
    {
        var result = Types.InAssembly(ArchitectureRules.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                [
                    ArchitectureRules.InfrastructureNamespace,
                    .. ArchitectureRules.ApiNamespaces,
                ])
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Application_should_not_depend_on_EntityFrameworkCore()
    {
        // Le interfacce repository restituiscono entità di dominio, mai IQueryable/EF:
        // i dettagli di persistenza non devono trapelare nel contratto applicativo.
        var result = Types.InAssembly(ArchitectureRules.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOn(ArchitectureRules.EntityFrameworkNamespace)
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Application_should_not_depend_on_AspNetCore()
    {
        // Le concern web (controller, model binding, HTTP) restano nell'API.
        var result = Types.InAssembly(ArchitectureRules.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOn(ArchitectureRules.AspNetCoreNamespace)
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Application_should_not_depend_on_cache_implementations()
    {
        // Il decoratore di caching usa solo l'astrazione HybridCache: FusionCache/Redis sono
        // dettagli della composition root (Infrastructure), non devono trapelare in Application.
        var result = Types.InAssembly(ArchitectureRules.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ArchitectureRules.CacheImplementationNamespaces)
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Application_should_not_depend_on_resilience_implementations()
    {
        // Il service di popolarità usa solo l'astrazione IBookPopularityClient: Polly e
        // Microsoft.Extensions.Http(.Resilience) — HttpClient tipizzato + pipeline retry/CB/timeout —
        // sono dettagli di Infrastructure, non devono trapelare in Application.
        var result = Types.InAssembly(ArchitectureRules.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ArchitectureRules.ResilienceImplementationNamespaces)
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Application_should_not_depend_on_hosting_or_channels()
    {
        // Il processamento asincrono espone in Application solo l'astrazione IBackgroundTaskQueue<T> (primitive
        // BCL). Il BackgroundService (Microsoft.Extensions.Hosting) e la coda su System.Threading.Channels sono
        // il meccanismo, confinato a Infrastructure — non deve trapelare in Application.
        var result = Types.InAssembly(ArchitectureRules.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ArchitectureRules.BackgroundProcessingImplementationNamespaces)
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Application_should_not_depend_on_messaging_implementations()
    {
        // L'outbox espone in Application solo l'astrazione IIntegrationEventPublisher (primitive BCL). L'SDK
        // Azure Service Bus (Azure.Messaging) e l'auth managed identity (Azure.Identity) sono il trasporto,
        // confinato a Infrastructure — non deve trapelare in Application.
        var result = Types.InAssembly(ArchitectureRules.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ArchitectureRules.MessagingImplementationNamespaces)
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Application_should_not_depend_on_key_vault_implementations()
    {
        // I secret del vault entrano in IConfiguration nella composition root dell'host (Api):
        // Application legge la config già risolta e non sa da dove arrivi un valore.
        var result = Types.InAssembly(ArchitectureRules.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ArchitectureRules.KeyVaultImplementationNamespaces)
            .GetResult();

        AssertArchitecture(result);
    }

    [Fact]
    public void Infrastructure_should_not_depend_on_key_vault_implementations()
    {
        // Vale anche per Infrastructure: il bootstrap della configurazione è dell'host (Api), non del
        // layer di persistenza/trasporto — che consuma IOptions/connection string già risolte.
        var result = Types.InAssembly(ArchitectureRules.InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ArchitectureRules.KeyVaultImplementationNamespaces)
            .GetResult();

        AssertArchitecture(result);
    }

    // ---- Infrastructure: Domain + Application, mai API ----------------------

    [Fact]
    public void Infrastructure_should_not_depend_on_Api()
    {
        var result = Types.InAssembly(ArchitectureRules.InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ArchitectureRules.ApiNamespaces)
            .GetResult();

        AssertArchitecture(result);
    }

    /// <summary>
    /// Asserisce sul risultato NetArchTest elencando i tipi colpevoli quando fallisce,
    /// così il messaggio dice subito *quale* tipo ha introdotto la dipendenza vietata.
    /// </summary>
    private static void AssertArchitecture(TestResult result)
    {
        var failing = result.FailingTypeNames is { Count: > 0 }
            ? string.Join(", ", result.FailingTypeNames)
            : "(nessuno)";

        Assert.True(
            result.IsSuccessful,
            $"Violazione delle regole di layering (vedi architecture.md). Tipi colpevoli: {failing}");
    }
}
