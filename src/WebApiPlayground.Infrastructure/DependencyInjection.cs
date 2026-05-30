using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Application.Interfaces;
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

        return services;
    }
}
