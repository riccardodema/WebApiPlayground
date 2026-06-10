using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Infrastructure.Outbox;
using Xunit;

namespace WebApiPlayground.Tests.Outbox;

/// <summary>
/// Unit del routing degli eventi di integrazione (<see cref="IntegrationEventHandler"/>): è il "consumatore"
/// logico condiviso dai due trasporti (in-process e consumer Service Bus), quindi instradare l'evento giusto
/// all'<see cref="IPopularityEnricher"/> è il comportamento centrale da fissare. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public class IntegrationEventHandlerTests
{
    private readonly Mock<IPopularityEnricher> _enricher = new();
    private readonly IntegrationEventHandler _sut;

    public IntegrationEventHandlerTests() =>
        _sut = new IntegrationEventHandler(_enricher.Object, NullLogger<IntegrationEventHandler>.Instance);

    [Fact]
    public async Task RoutesPopularityEnrichmentRequested_ToEnricher_WithItsBookId()
    {
        await _sut.HandleAsync(new PopularityEnrichmentRequested(7, TraceParent: null), CancellationToken.None);

        _enricher.Verify(e => e.EnrichAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnroutedEventType_Throws_AndDoesNotEnrich()
    {
        // Un evento serializzabile ma non instradato (bug: aggiunto al serializzatore, non al routing) → fail-fast.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.HandleAsync(new UnroutedEvent(), CancellationToken.None));

        _enricher.Verify(
            e => e.EnrichAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed record UnroutedEvent : IntegrationEvent
    {
        public override string EventType => "UnroutedEvent";
    }
}
