using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Services;
using WebApiPlayground.Application.Validation;

namespace WebApiPlayground.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IBooksService, BooksService>();

        // Validator FluentValidation registrati come Singleton: sono stateless (nessuna
        // dipendenza scoped) e così risolvibili sia dal ValidationFilter sia dallo
        // schema transformer OpenAPI, che gira sul root provider.
        services.AddValidatorsFromAssemblyContaining<CreateBookDtoValidator>(ServiceLifetime.Singleton);

        return services;
    }
}
