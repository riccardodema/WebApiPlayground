using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.ServiceBus;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Outbox;

/// <summary>
/// Factory che attiva il <b>trasporto Azure Service Bus reale</b> tramite l'<b>emulatore ufficiale</b> (immagine
/// Docker, via Testcontainers) — niente account Azure necessario. Configura la connection string dell'emulatore
/// (→ <c>ServiceBusOptions.IsConfigured</c> = true → publisher + consumer ASB), tiene <b>acceso</b> il vero
/// <c>OutboxDispatcher</c> (così l'outbox pubblica sul broker) e usa la coda di default dell'emulatore
/// (<c>queue.1</c>). Container isolato (proprio MsSql ereditato dalla base + emulatore dedicato). Usata solo da
/// <see cref="ServiceBusOutboxTests"/>.
/// </summary>
public sealed class ServiceBusEnabledApiFactory : PlaygroundApiFactory, IAsyncLifetime
{
    // Coda predefinita fornita dalla config di default del modulo Testcontainers.ServiceBus.
    private const string EmulatorDefaultQueue = "queue.1";

    // Immagine pinnata esplicitamente (= ex-default del modulo): Testcontainers 4.12 ha deprecato il costruttore
    // senza immagine. L'emulatore avvia internamente anche un container MsSql di supporto.
    private readonly ServiceBusContainer _serviceBus =
        new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .Build();

    // Tiene acceso il vero dispatcher: deve pollare l'outbox e PUBBLICARE i messaggi su Service Bus.
    protected override bool DisableOutboxDispatcher => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Connection string dell'emulatore → attiva il trasporto ASB (publisher + consumer disaccoppiato).
            ["ServiceBus:ConnectionString"] = _serviceBus.GetConnectionString(),
            ["ServiceBus:QueueName"] = EmulatorDefaultQueue,
            // Polling stretto: l'outbox pubblica in fretta → il test attende poco (con timeout generoso).
            ["Outbox:PollingInterval"] = "00:00:00.200",
        }));
        base.ConfigureWebHost(builder);
    }

    // L'emulatore va avviato PRIMA che la base costruisca l'host (ConfigureWebHost legge la connection string).
    // Reimplementazione esplicita di IAsyncLifetime: rimappa per il tipo derivato delegando ai metodi base.
    async Task IAsyncLifetime.InitializeAsync()
    {
        await _serviceBus.StartAsync();
        await base.InitializeAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _serviceBus.DisposeAsync();
    }
}

/// <summary>
/// End-to-end del <b>trasporto broker</b> (PR-2) contro l'emulatore Service Bus: una <c>POST /books</c> scrive il
/// libro + la riga outbox; il vero <c>OutboxDispatcher</c> la <b>pubblica</b> sulla coda; il consumer disaccoppiato
/// la <b>riceve</b> e arricchisce, persistendo lo snapshot. Prova il flusso completo publish→consume→enrich oltre
/// il broker, senza account Azure. Deterministico senza essere a tempo: ambiente isolato (emulatore dedicato) +
/// attesa dell'esito con timeout generoso. Nella collection "Integration" → serializzato col listener OTel [L18].
/// Vedi <c>.claude/context/outbox.md</c> e <c>.claude/lessons.md</c> [L24].
/// </summary>
[Collection("Integration")]
public class ServiceBusOutboxTests : IClassFixture<ServiceBusEnabledApiFactory>, IAsyncLifetime
{
    // Generoso: copre il primo recapito dell'emulatore (più lento a freddo dei container "puri").
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ServiceBusEnabledApiFactory _factory;
    private readonly HttpClient _writeClient;

    public ServiceBusOutboxTests(ServiceBusEnabledApiFactory factory)
    {
        _factory = factory;
        _writeClient = factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedAuthorAsync(string fullName = "Frank Herbert")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var author = new Author { FullName = fullName };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        return author.Id;
    }

    private async Task<BookPopularitySnapshot?> WaitForSnapshotAsync(int bookId)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
                var snapshot = await db.BookPopularitySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.BookId == bookId);
                if (snapshot is not null)
                    return snapshot;
            }
            await Task.Delay(250);
        }

        return null;
    }

    private async Task<bool> AllOutboxProcessedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        return !await db.OutboxMessages.AsNoTracking().AnyAsync(m => m.ProcessedAt == null);
    }

    [Fact]
    public async Task OutboxPublishesToServiceBus_ConsumerEnrichesPostedBook()
    {
        var authorId = await SeedAuthorAsync();

        var create = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("Dune", authorId));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(created);

        // NESSUN drain manuale: dispatcher reale pubblica outbox→ASB, consumer disaccoppiato riceve→arricchisce.
        var snapshot = await WaitForSnapshotAsync(created!.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(created.Id, snapshot!.BookId);
        Assert.Equal("Open Library", snapshot.Source);

        // La riga outbox è stata pubblicata sul broker e marcata processata (consegna durevole all'ASB).
        Assert.True(await AllOutboxProcessedAsync());
    }
}
