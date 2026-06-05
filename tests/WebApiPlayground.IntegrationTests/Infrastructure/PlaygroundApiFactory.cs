using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using WebApiPlayground.Application.Caching;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.Infrastructure.Popularity;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Infrastructure;

public class PlaygroundApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sqlContainer = new MsSqlBuilder().Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Alza i limiti a valori altissimi: il rate limiter è un singleton in-memory condiviso da
        // tutta la collection, e le richieste autenticate condividono la stessa partizione
        // ("test-user") — coi limiti reali la suite cumulativa farebbe 429. I test dedicati al rate
        // limiting li riabbassano via WithWebHostBuilder. Vedi .claude/lessons.md [L15].
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Read:PermitLimit"] = "1000000",
            ["RateLimiting:Write:PermitLimit"] = "1000000",
        }));

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

            // Sostituisce il primary handler del client di popolarità con uno stub di successo: i test
            // dell'endpoint non toccano la rete reale (Open Library). I test di indisponibilità installano
            // via WithWebHostBuilder uno stub che fallisce sempre → 503. Vedi .claude/context/resilience.md.
            services.AddHttpClient<IBookPopularityClient, OpenLibraryPopularityClient>()
                .ConfigurePrimaryHttpMessageHandler(() => PopularityHttpStub.AlwaysOk());
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

        // I test fanno seed direttamente sul DB (bypassando l'API), quindi non passano per
        // l'invalidazione del decoratore di caching: senza questo flush la cache L1 — condivisa
        // dalla factory tra i test della collection — restituirebbe dati stale → test flaky.
        // Vedi .claude/lessons.md [L11].
        var cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        await cache.RemoveByTagAsync(BookCacheKeys.Books);
    }
}
