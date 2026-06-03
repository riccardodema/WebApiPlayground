using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.Caching;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Services;
using WebApiPlayground.Application.Validation;

namespace WebApiPlayground.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // IBooksService = CachingBooksService che decora il BooksService concreto.
        // Decoratore via factory (DI nativa, niente Scrutor): risolve il concreto + l'astrazione
        // HybridCache (registrata dal layer Infrastructure, risoluzione lazy → l'ordine in
        // Program.cs non conta).
        services.AddScoped<BooksService>();
        services.AddScoped<IBooksService>(sp => new CachingBooksService(
            sp.GetRequiredService<BooksService>(),
            sp.GetRequiredService<HybridCache>(),
            sp.GetRequiredService<ILogger<CachingBooksService>>()));

        // Validator FluentValidation registrati come Singleton: sono stateless (nessuna
        // dipendenza scoped) e così risolvibili sia dal ValidationFilter sia dallo
        // schema transformer OpenAPI, che gira sul root provider.
        services.AddValidatorsFromAssemblyContaining<CreateBookDtoValidator>(ServiceLifetime.Singleton);

        return services;
    }
}
