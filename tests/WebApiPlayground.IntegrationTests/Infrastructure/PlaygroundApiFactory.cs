using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;
using WebApiPlayground.Application.Caching;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Infrastructure.Outbox;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.Infrastructure.Popularity;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace WebApiPlayground.IntegrationTests.Infrastructure;

public class PlaygroundApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Immagine pinnata esplicitamente (= ex-default del modulo): Testcontainers 4.12 ha deprecato il
    // costruttore senza immagine; pinnare il tag rende il container riproducibile.
    private readonly MsSqlContainer _sqlContainer =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <summary>
    /// Se <c>true</c> (default) l'<c>OutboxDispatcher</c> hosted è rimosso e il processing si guida via
    /// <see cref="DrainOutboxAsync"/> (deterministico, niente polling). La sottoclasse
    /// <c>DispatcherEnabledApiFactory</c> lo riattiva (container isolato) per testare il loop di hosting reale.
    /// </summary>
    protected virtual bool DisableOutboxDispatcher => true;

    /// <summary>
    /// Se <c>true</c> (default) il <c>DbContext</c> dell'app viene ripuntato sul container SQL del test.
    /// La factory Key Vault (<c>KeyVaultEnabledApiFactory</c>) lo DISATTIVA: la connection string deve
    /// arrivare all'app SOLO dal vault (emulatore), così l'e2e prova davvero il config provider.
    /// </summary>
    protected virtual bool OverrideDbContextWithTestContainer => true;

    /// <summary>Connection string del container SQL del test (per i derivati, es. seed del vault).</summary>
    protected string SqlConnectionString => _sqlContainer.GetConnectionString();

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
            if (OverrideDbContextWithTestContainer)
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<PlaygroundDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);

                services.AddDbContext<PlaygroundDbContext>(options =>
                    options.UseSqlServer(_sqlContainer.GetConnectionString()));
            }

            // Di default niente OutboxDispatcher in background nei test: il polling continuo su un DB condiviso
            // fra tutta la collection interferirebbe fra i test (e correrebbe con EnsureCreated allo startup). Il
            // processing si guida esplicitamente via DrainOutboxAsync → deterministico. La sottoclasse dedicata
            // (DispatcherEnabledApiFactory) lo RIATTIVA, su un container isolato, per testare il loop reale.
            if (DisableOutboxDispatcher)
            {
                var dispatcher = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxDispatcher));
                if (dispatcher is not null)
                    services.Remove(dispatcher);
            }

            // Aggiunge il controller di test (ThrowingTestController) alla pipeline reale,
            // per esercitare GlobalExceptionHandler end-to-end senza endpoint fittizi in produzione.
            services.AddControllers().AddApplicationPart(typeof(PlaygroundApiFactory).Assembly);

            // Sostituisce il JWT Bearer Entra ID con uno schema di test impostato come default:
            // i test non richiedono un tenant reale e simulano i claim via header.
            services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Sostituisce il primary handler del client di popolarità (CONCRETO; l'astrazione è il decoratore
            // di caching) con uno stub di successo: i test dell'endpoint non toccano la rete reale (Open Library).
            // I test di indisponibilità installano via WithWebHostBuilder uno stub che fallisce sempre → 503.
            services.AddHttpClient<OpenLibraryPopularityClient>()
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

    /// <summary>
    /// Processa l'outbox in modo deterministico (un batch) e ritorna quanti messaggi sono stati esaminati.
    /// Nei test sostituisce il polling del dispatcher (disattivato): POST → DrainOutboxAsync() → asserzioni,
    /// senza attese/timeout. Vedi <c>.claude/context/outbox.md</c>.
    /// </summary>
    public async Task<int> DrainOutboxAsync()
    {
        using var scope = Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
        return await processor.ProcessPendingAsync(CancellationToken.None);
    }

    public new async Task DisposeAsync()
    {
        await _sqlContainer.DisposeAsync();
    }

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        // Ordine FK: gli snapshot referenziano i libri (cancellare i libri li farebbe cascadere comunque,
        // ma l'esplicito tiene il reset leggibile e indipendente dalla cascade).
        await db.Database.ExecuteSqlRawAsync("DELETE FROM BookPopularitySnapshots");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Books");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Authors");
        // Outbox: nessuna FK verso le altre tabelle, ma va svuotata col DB per isolare i test fra loro
        // (un messaggio non processato di un test non deve essere consegnato in quello successivo).
        await db.Database.ExecuteSqlRawAsync("DELETE FROM OutboxMessages");

        // I test fanno seed direttamente sul DB (bypassando l'API), quindi non passano per
        // l'invalidazione del decoratore di caching: senza questo flush la cache L1 — condivisa
        // dalla factory tra i test della collection — restituirebbe dati stale → test flaky.
        // Vedi .claude/lessons.md [L11].
        var cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        await cache.RemoveByTagAsync(BookCacheKeys.Books);

        // Stesso motivo per la cache di popolarità (tag dedicato, backing FusionCache): va svuotata col DB,
        // altrimenti una risposta cache-ata in un test trapela nel successivo. Vedi [L11]/[L20].
        var fusionCache = scope.ServiceProvider.GetRequiredService<IFusionCache>();
        await fusionCache.RemoveByTagAsync(PopularityCacheKeys.Tag);
    }
}
