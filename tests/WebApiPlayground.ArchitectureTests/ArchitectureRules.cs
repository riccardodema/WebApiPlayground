using System.Reflection;
using WebApiPlayground.Api.Controllers;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.ArchitectureTests;

/// <summary>
/// Punto unico di verità per le regole di layering: namespace radice dei 4 layer e
/// assembly anchor (un tipo per assembly da cui NetArchTest carica i metadati).
/// Tenere allineato a <c>.claude/context/architecture.md</c>.
/// </summary>
internal static class ArchitectureRules
{
    // Namespace radice di ogni layer. NetArchTest fa match per prefisso sul full name
    // del tipo dipeso, quindi "WebApiPlayground.Application" copre tutti i sotto-namespace.
    internal const string DomainNamespace = "WebApiPlayground.Domain";
    internal const string ApplicationNamespace = "WebApiPlayground.Application";
    internal const string InfrastructureNamespace = "WebApiPlayground.Infrastructure";

    // Tutto il layer API (controller inclusi) vive sotto WebApiPlayground.Api: un solo
    // namespace radice da vietare ai layer inferiori. Array per estendibilità futura.
    internal static readonly string[] ApiNamespaces = ["WebApiPlayground.Api"];

    // Dipendenze tecnologiche che NON devono risalire oltre il loro layer.
    internal const string EntityFrameworkNamespace = "Microsoft.EntityFrameworkCore";
    internal const string AspNetCoreNamespace = "Microsoft.AspNetCore";

    // Backing store della cache: Application deve dipendere solo dall'astrazione HybridCache
    // (Microsoft.Extensions.Caching.Hybrid), MAI dai concreti FusionCache/Redis (che vivono
    // nella composition root, layer Infrastructure).
    internal static readonly string[] CacheImplementationNamespaces =
        ["ZiggyCreatures.Caching.Fusion", "StackExchange.Redis", "Microsoft.Extensions.Caching.StackExchangeRedis"];

    // Resilienza + HttpClient: Application dipende solo dall'astrazione IBookPopularityClient. Polly e
    // Microsoft.Extensions.Http(.Resilience) sono dettagli del client tipizzato e della pipeline, confinati
    // alla composition root (Infrastructure). Stesso principio della cache.
    internal static readonly string[] ResilienceImplementationNamespaces =
        ["Polly", "Microsoft.Extensions.Http"];

    // Assembly anchor: un tipo pubblico stabile per ciascun layer.
    internal static readonly Assembly DomainAssembly = typeof(Book).Assembly;
    internal static readonly Assembly ApplicationAssembly = typeof(IBookRepository).Assembly;
    internal static readonly Assembly InfrastructureAssembly = typeof(PlaygroundDbContext).Assembly;
    internal static readonly Assembly ApiAssembly = typeof(BooksController).Assembly;
}
