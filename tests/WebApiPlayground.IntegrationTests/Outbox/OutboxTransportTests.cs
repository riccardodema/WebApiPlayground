using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Outbox;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Outbox;

/// <summary>
/// Verifica il <b>disaccoppiamento</b> introdotto da PR-2: il <c>OutboxProcessor</c> non arricchisce più inline,
/// ma <i>pubblica</i> l'evento sul trasporto astratto (<see cref="IIntegrationEventPublisher"/>). Sostituendo il
/// trasporto con un <see cref="RecordingIntegrationEventPublisher"/> (nessun broker, nessun arricchimento) si
/// dimostra il contratto del seam senza Service Bus: evento pubblicato + riga marcata processata, ma
/// <b>nessuno snapshot</b> (l'enrichment è delegato al trasporto, qui un fake che non fa nulla). L'end-to-end col
/// broker reale è in <see cref="ServiceBusOutboxTests"/> (emulatore). Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
[Collection("Integration")]
public class OutboxTransportTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly PlaygroundApiFactory _factory;

    public OutboxTransportTests(PlaygroundApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Processor_PublishesEventToTransport_AndMarksProcessed_WithoutEnrichingInline()
    {
        var recording = new RecordingIntegrationEventPublisher();

        // Sostituisce il trasporto di default (in-process) con il fake che registra e non arricchisce.
        await using var customized = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IIntegrationEventPublisher>();
                services.AddSingleton<IIntegrationEventPublisher>(recording);
            }));

        // Seed di un libro + riga outbox non processata (stesso DB container della factory).
        int bookId;
        using (var scope = customized.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
            var author = new Author { FullName = "Frank Herbert" };
            db.Authors.Add(author);
            await db.SaveChangesAsync();

            var book = new Book { Title = "Dune", AuthorId = author.Id };
            db.Books.Add(book);
            await db.SaveChangesAsync();
            bookId = book.Id;

            db.OutboxMessages.Add(new OutboxMessage
            {
                Type = PopularityEnrichmentRequested.TypeName,
                Payload = JsonSerializer.Serialize(new PopularityEnrichmentRequested(bookId, null), SerializerOptions),
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Drain deterministico sul host personalizzato (così usa il trasporto fake).
        using (var scope = customized.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
            var examined = await processor.ProcessPendingAsync(CancellationToken.None);
            Assert.True(examined >= 1);
        }

        // Il processore ha pubblicato l'evento corretto sul trasporto…
        var published = Assert.Single(recording.Published);
        var enrichment = Assert.IsType<PopularityEnrichmentRequested>(published);
        Assert.Equal(bookId, enrichment.BookId);

        using (var scope = customized.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();

            // …la riga outbox è marcata processata (il publish è andato a buon fine)…
            var message = await db.OutboxMessages.AsNoTracking()
                .FirstAsync(m => m.Type == PopularityEnrichmentRequested.TypeName);
            Assert.NotNull(message.ProcessedAt);

            // …ma NON c'è snapshot: l'arricchimento è delegato al trasporto (il fake non fa nulla) → seam disaccoppiato.
            var snapshot = await db.BookPopularitySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.BookId == bookId);
            Assert.Null(snapshot);
        }
    }
}
