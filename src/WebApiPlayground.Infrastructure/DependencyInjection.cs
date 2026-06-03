using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Infrastructure.HealthChecks;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.Infrastructure.Repositories;

namespace WebApiPlayground.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlaygroundDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("Default")));

        services.AddScoped<IBookRepository, BookRepository>();

        // Health check di readiness sul DB: verifica che il DbContext riesca a connettersi.
        // Tagged "ready" → esposto solo dal probe /health/ready (vedi Api/HealthChecks).
        services.AddHealthChecks()
            .AddDbContextCheck<PlaygroundDbContext>(name: "database", tags: [HealthCheckTags.Ready]);

        return services;
    }
}
