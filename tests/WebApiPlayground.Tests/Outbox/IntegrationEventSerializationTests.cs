using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Infrastructure.Outbox;
using Xunit;

namespace WebApiPlayground.Tests.Outbox;

/// <summary>
/// Unit del contratto JSON condiviso (<see cref="IntegrationEventSerialization"/>): è la sorgente unica usata da
/// outbox (scrittura/lettura riga) e dal trasporto Service Bus (body messaggio). Un round-trip che perde un campo
/// o una mappa Type→concreto sbagliata romperebbe la consegna <b>oltre il confine durevole</b>, quindi è coperto
/// esplicitamente. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public class IntegrationEventSerializationTests
{
    [Theory]
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")]
    [InlineData(null)]
    public void RoundTrips_PopularityEnrichmentRequested_PreservingAllFields(string? traceParent)
    {
        var original = new PopularityEnrichmentRequested(42, traceParent);

        var json = IntegrationEventSerialization.Serialize(original);
        var roundTripped = IntegrationEventSerialization.Deserialize(original.EventType, json);

        var typed = Assert.IsType<PopularityEnrichmentRequested>(roundTripped);
        Assert.Equal(42, typed.BookId);
        Assert.Equal(traceParent, typed.TraceParent);
    }

    [Fact]
    public void Deserialize_UnknownEventType_Throws()
    {
        // Tipo non mappato → fail-fast: il chiamante (OutboxProcessor/consumer) isola il messaggio e lo conta poison.
        var ex = Assert.Throws<InvalidOperationException>(
            () => IntegrationEventSerialization.Deserialize("UnknownEventType", "{}"));
        Assert.Contains("UnknownEventType", ex.Message);
    }

    [Fact]
    public void Deserialize_NullPayload_Throws()
    {
        // Payload JSON "null" → deserializza a null → errore esplicito (riga outbox corrotta).
        Assert.Throws<InvalidOperationException>(
            () => IntegrationEventSerialization.Deserialize(PopularityEnrichmentRequested.TypeName, "null"));
    }
}
