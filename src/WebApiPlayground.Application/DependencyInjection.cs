using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Services;

namespace WebApiPlayground.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IBooksService, BooksService>();
        return services;
    }
}
