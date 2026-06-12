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

    /// <summary>Connection string dell'emulatore, per i test che parlano col broker direttamente (es. DLQ).</summary>
    public string BrokerConnectionString => _serviceBus.GetConnectionString();

    /// <summary>Nome della coda usata dal consumer in questi test.</summary>
    public string QueueName => EmulatorDefaultQueue;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting (host configuration), NON ConfigureAppConfiguration: la scelta del trasporto avviene
        // in FASE BUILDER (AddInfrastructure legge ServiceBus:ConnectionString eager per decidere ASB vs
        // in-process) e i provider aggiunti via ConfigureAppConfiguration dalla factory arrivano solo DOPO
        // → sarebbero invisibili lì e il test girerebbe in-process passando per il motivo sbagliato
        // (l'handler è lo stesso). Il probe Transport_is_really_Azure_Service_Bus lo fa rispettare.
        builder.UseSetting("ServiceBus:ConnectionString", _serviceBus.GetConnectionString());
        builder.UseSetting("ServiceBus:QueueName", EmulatorDefaultQueue);
        // Polling stretto: l'outbox pubblica in fretta → il test attende poco (con timeout generoso).
        builder.UseSetting("Outbox:PollingInterval", "00:00:00.200");
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
    public void Transport_is_really_Azure_Service_Bus()
    {
        // Probe strutturale: il ServiceBusClient è registrato SOLO quando il trasporto ASB è attivo
        // (AddServiceBusTransport). Senza questo check il test e2e sotto passerebbe anche col fallback
        // in-process (stesso handler, stesso snapshot) — cioè senza mai toccare il broker.
        Assert.NotNull(_factory.Services.GetService<Azure.Messaging.ServiceBus.ServiceBusClient>());
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

    [Fact]
    public async Task Malformed_message_is_abandoned_until_it_dead_letters()
    {
        // Messaggio "poison" mandato direttamente sulla coda (bypassa l'outbox): il consumer non riesce
        // a deserializzarlo → Abandon (mai Complete) → il broker lo riconsegna fino a maxDeliveryCount,
        // poi lo sposta in DEAD-LETTER: il messaggio non va perso né può ciclare per sempre, e la coda
        // torna pulita per i messaggi buoni. Vedi .claude/context/outbox.md (sez. PR-2).
        await using var client = new Azure.Messaging.ServiceBus.ServiceBusClient(_factory.BrokerConnectionString);

        var sender = client.CreateSender(_factory.QueueName);
        var poison = new Azure.Messaging.ServiceBus.ServiceBusMessage(BinaryData.FromString("definitely-not-an-event"))
        {
            Subject = "Garbage.Event",
        };
        await sender.SendMessageAsync(poison);

        var dlqReceiver = client.CreateReceiver(_factory.QueueName, new Azure.Messaging.ServiceBus.ServiceBusReceiverOptions
        {
            SubQueue = Azure.Messaging.ServiceBus.SubQueue.DeadLetter,
        });

        Azure.Messaging.ServiceBus.ServiceBusReceivedMessage? dead = null;
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (dead is null && DateTimeOffset.UtcNow < deadline)
            dead = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(dead);
        Assert.Equal("Garbage.Event", dead!.Subject);
        await dlqReceiver.CompleteMessageAsync(dead); // pulizia: non lasciare il poison nel DLQ condiviso
    }
}
