using System.Collections.Concurrent;
using WebApiPlayground.Application.Outbox;

namespace WebApiPlayground.IntegrationTests.Outbox;

/// <summary>
/// Test double del trasporto: registra gli eventi pubblicati e <b>non</b> li gestisce (non arricchisce). Sostituendo
/// <see cref="IIntegrationEventPublisher"/> con questo si verifica il <i>seam</i> dell'outbox in isolamento, senza
/// un broker: il <c>OutboxProcessor</c> deve pubblicare l'evento corretto e marcare la riga processata, mentre
/// l'assenza di snapshot dimostra che l'arricchimento è ora delegato al trasporto (non più fatto inline come in PR-1).
/// </summary>
public sealed class RecordingIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly ConcurrentQueue<IntegrationEvent> _published = new();

    public IReadOnlyCollection<IntegrationEvent> Published => _published.ToArray();

    public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        _published.Enqueue(integrationEvent);
        return Task.CompletedTask; // successo "silenzioso": nessun lavoro a valle (è ciò che il test verifica)
    }
}
