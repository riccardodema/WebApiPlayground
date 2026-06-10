using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebApiPlayground.Infrastructure.Outbox;

namespace WebApiPlayground.Infrastructure.Outbox.ServiceBus;

/// <summary>
/// Consumer <b>disaccoppiato</b> della coda Service Bus (attivo solo se ASB è configurato): riceve i messaggi
/// pubblicati dall'outbox e li gestisce riusando l'<see cref="IntegrationEventHandler"/> (stessa logica del path
/// in-process). È il pezzo che rende l'arricchimento indipendente dalla write — e già predisposto per uno split
/// in un Worker separato (nessuna dipendenza dall'API).
///
/// <para><b>At-least-once lato broker:</b> il settlement è manuale (<c>AutoCompleteMessages = false</c>).
/// Successo → <c>CompleteMessageAsync</c> (rimosso dalla coda); fallimento → <c>AbandonMessageAsync</c> → ASB
/// ridistribuisce, fino a <c>MaxDeliveryCount</c> della coda → dead-letter. L'handler è idempotente (upsert dello
/// snapshot keyed su <c>BookId</c>), quindi una redelivery è sicura.</para>
///
/// <para><b>Scope per messaggio:</b> l'handler dipende da servizi scoped (enricher → repository → DbContext),
/// quindi ogni messaggio è gestito in un proprio DI scope (isolamento, niente DbContext condiviso fra messaggi
/// concorrenti).</para>
///
/// <para>Lo span "Popularity.Enrich" parte dentro l'handler dal <c>traceparent</c> trasportato nell'evento: la
/// trace della write originaria prosegue <b>oltre il broker</b>. Vedi <c>.claude/context/outbox.md</c>.</para>
/// </summary>
internal sealed class ServiceBusIntegrationEventConsumer : BackgroundService
{
    // Attesa fra i tentativi di StartProcessing quando il broker non è ancora pronto (es. emulatore che parte
    // insieme all'app in docker-compose) o è temporaneamente irraggiungibile.
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ServiceBusProcessor _processor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServiceBusIntegrationEventConsumer> _logger;

    public ServiceBusIntegrationEventConsumer(
        ServiceBusClient client,
        IServiceScopeFactory scopeFactory,
        IOptions<ServiceBusOptions> options,
        ILogger<ServiceBusIntegrationEventConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _logger = logger;

        _processor = client.CreateProcessor(options.Value.QueueName, new ServiceBusProcessorOptions
        {
            // Settlement manuale: completiamo solo dopo aver gestito con successo → at-least-once.
            AutoCompleteMessages = false,
            MaxConcurrentCalls = options.Value.MaxConcurrentCalls,
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnProcessorErrorAsync;

        await StartProcessingWithRetryAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
            return;

        _logger.LogInformation(
            "ServiceBusIntegrationEventConsumer started (queue {Queue})", _processor.EntityPath);

        try
        {
            // Il processore lavora sul proprio loop interno; restiamo vivi finché non arriva lo shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown richiesto: usciamo puliti (lo stop avviene nel finally)
        }
        finally
        {
            // StopProcessingAsync attende che i messaggi in volo finiscano (drain graceful) prima di chiudere.
            await _processor.StopProcessingAsync(CancellationToken.None);
            _logger.LogInformation("ServiceBusIntegrationEventConsumer stopping");
        }
    }

    /// <summary>
    /// Avvia il processore riprovando finché il broker non è raggiungibile: all'avvio l'emulatore/namespace può
    /// non essere ancora pronto (ordine di boot in docker-compose) o essere temporaneamente giù. Senza retry
    /// un'eccezione qui abbatterebbe l'host (<c>BackgroundServiceExceptionBehavior.StopHost</c>).
    /// </summary>
    private async Task StartProcessingWithRetryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _processor.StartProcessingAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Service Bus not ready (queue {Queue}) — retrying StartProcessing in {Delay}",
                    _processor.EntityPath, StartRetryDelay);
                try { await Task.Delay(StartRetryDelay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        try
        {
            var integrationEvent = IntegrationEventSerialization.Deserialize(message.Subject, message.Body.ToString());

            // Scope per-messaggio: enricher/DbContext freschi e isolati (come il dispatcher in-process).
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IntegrationEventHandler>();
            await handler.HandleAsync(integrationEvent, args.CancellationToken);

            await args.CompleteMessageAsync(message, args.CancellationToken);
        }
        catch (Exception ex)
        {
            // Isola il singolo messaggio: abbandona → ASB lo ridistribuisce (handler idempotente). Oltre il
            // MaxDeliveryCount della coda finisce in dead-letter, dove resta per diagnostica senza bloccare gli altri.
            _logger.LogWarning(ex,
                "Failed to handle Service Bus message {MessageId} ({Subject}) — abandoning for redelivery",
                message.MessageId, message.Subject);
            await args.AbandonMessageAsync(message, cancellationToken: args.CancellationToken);
        }
    }

    private Task OnProcessorErrorAsync(ProcessErrorEventArgs args)
    {
        // Errori a livello di connessione/processore (non del singolo messaggio): il processore si auto-recupera;
        // qui logghiamo per osservabilità.
        _logger.LogError(args.Exception,
            "Service Bus processor error (source {Source}, entity {Entity})", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _processor.DisposeAsync();
    }
}
