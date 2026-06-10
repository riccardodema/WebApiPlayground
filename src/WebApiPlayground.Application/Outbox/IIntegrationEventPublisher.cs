namespace WebApiPlayground.Application.Outbox;

/// <summary>
/// <b>Trasporto</b> di un evento di integrazione già letto in modo durevole dalla outbox: astrae <i>dove</i> va
/// consegnato l'evento, separando il pattern outbox (lato DB) dal canale di consegna. Due implementazioni,
/// scelte nella composition root in base alla config (come Redis/OTLP):
/// <list type="bullet">
///   <item><b>In-process</b> (default): gestisce l'evento subito nello stesso processo (instrada all'handler).
///   Il <c>OutboxProcessor</c> marca il messaggio consegnato solo se questa ritorna senza errori → at-least-once
///   durevole in-process (comportamento di PR-1).</item>
///   <item><b>Azure Service Bus</b> (se configurato): pubblica l'evento sul broker; un <i>consumer disaccoppiato</i>
///   lo riceve e lo gestisce. Il messaggio è "consegnato" (e marcato processato) quando il broker lo accetta in
///   modo durevole → l'arricchimento diventa asincrono e indipendente dalla write.</item>
/// </list>
/// Vive in Application e usa solo primitive BCL: l'implementazione (in-process o SDK Service Bus) resta in
/// Infrastructure dietro questa interfaccia. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Consegna l'evento al trasporto configurato. <b>Deve</b> propagare l'eccezione in caso di fallimento: è il
    /// segnale con cui il <c>OutboxProcessor</c> NON marca il messaggio processato e lo riprova (at-least-once).
    /// </summary>
    Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken);
}
