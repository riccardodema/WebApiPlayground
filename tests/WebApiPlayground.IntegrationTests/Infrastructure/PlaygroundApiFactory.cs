using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using WebApiPlayground.Infrastructure.Persistence;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Infrastructure;

public class PlaygroundApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sqlContainer = new MsSqlBuilder().Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<PlaygroundDbContext>));
            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddDbContext<PlaygroundDbContext>(options =>
                options.UseSqlServer(_sqlContainer.GetConnectionString()));

            // Aggiunge il controller di test (ThrowingTestController) alla pipeline reale,
            // per esercitare GlobalExceptionHandler end-to-end senza endpoint fittizi in produzione.
            services.AddControllers().AddApplicationPart(typeof(PlaygroundApiFactory).Assembly);

            // Sostituisce il JWT Bearer Entra ID con uno schema di test impostato come default:
            // i test non richiedono un tenant reale e simulano i claim via header.
            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    /// <summary>Client anonimo: nessun header di auth → 401.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    /// <summary>Client con token delegato (claim <c>scp</c>): es. "Books.Read" o "Books.ReadWrite".</summary>
    public HttpClient CreateClientWithScope(string scope)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, scope);
        return client;
    }

    /// <summary>Client con app permission (claim <c>roles</c>): es. "Books.Read.All" o "Books.ReadWrite.All".</summary>
    public HttpClient CreateClientWithAppRoles(string roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    public async Task InitializeAsync()
    {
        await _sqlContainer.StartAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public new async Task DisposeAsync()
    {
        await _sqlContainer.DisposeAsync();
    }

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Books");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Authors");
    }
}
