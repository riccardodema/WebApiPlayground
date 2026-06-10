namespace WebApiPlayground.Infrastructure.Outbox.ServiceBus;

/// <summary>
/// Configurazione del trasporto Azure Service Bus dell'outbox (sezione <c>ServiceBus</c>). <b>Config-gated</b>
/// come Redis/OTLP: se non configurato (<see cref="IsConfigured"/> = false) l'outbox usa il trasporto in-process
/// e nessun client/consumer ASB viene registrato. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Connection string completa (con SAS). Pensata per <b>emulatore/locale</b>: in produzione si preferisce
    /// <see cref="FullyQualifiedNamespace"/> + managed identity (no segreti). Se valorizzata ha la precedenza.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// FQDN del namespace (es. <c>my-ns.servicebus.windows.net</c>). Se valorizzato (e nessuna connection string)
    /// il client si autentica con <c>DefaultAzureCredential</c> (managed identity in Azure) → <b>nessun segreto</b>.
    /// </summary>
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>Nome della coda su cui pubblicare e da cui consumare gli eventi di integrazione.</summary>
    public string QueueName { get; set; } = "popularity-enrichment";

    /// <summary>
    /// Messaggi elaborati in parallelo dal consumer. 1 = ordine preservato (FIFO), più alto = throughput maggiore.
    /// L'handler è idempotente, quindi il parallelismo è sicuro; resta basso di default per semplicità diagnostica.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 1;

    /// <summary>
    /// Configurato = c'è un endpoint Service Bus (connection string o namespace). Usato dalla composition root per
    /// scegliere il trasporto: vero → publisher + consumer ASB; falso → trasporto in-process (default).
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConnectionString) || !string.IsNullOrWhiteSpace(FullyQualifiedNamespace);
}
