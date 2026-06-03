using System.Reflection;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Controllers;
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

    // L'assembly API ospita due namespace radice: WebApiPlayground.Api.* (extension,
    // middleware, ...) e WebApiPlayground.Controllers (i controller). Vanno entrambi
    // vietati ai layer inferiori, altrimenti un riferimento ai controller sfuggirebbe.
    internal static readonly string[] ApiNamespaces =
        ["WebApiPlayground.Api", "WebApiPlayground.Controllers"];

    // Dipendenze tecnologiche che NON devono risalire oltre il loro layer.
    internal const string EntityFrameworkNamespace = "Microsoft.EntityFrameworkCore";
    internal const string AspNetCoreNamespace = "Microsoft.AspNetCore";

    // Assembly anchor: un tipo pubblico stabile per ciascun layer.
    internal static readonly Assembly DomainAssembly = typeof(Book).Assembly;
    internal static readonly Assembly ApplicationAssembly = typeof(IBookRepository).Assembly;
    internal static readonly Assembly InfrastructureAssembly = typeof(PlaygroundDbContext).Assembly;
    internal static readonly Assembly ApiAssembly = typeof(BooksController).Assembly;
}
