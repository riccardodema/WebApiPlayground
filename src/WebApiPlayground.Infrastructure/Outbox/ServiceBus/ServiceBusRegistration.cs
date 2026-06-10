using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebApiPlayground.Application.Outbox;

namespace WebApiPlayground.Infrastructure.Outbox.ServiceBus;

/// <summary>
/// Wiring del trasporto Azure Service Bus dell'outbox. Chiamato dalla composition root <b>solo se ASB è
/// configurato</b> (<see cref="ServiceBusOptions.IsConfigured"/>); altrimenti resta il trasporto in-process.
/// Registra il <see cref="ServiceBusClient"/> (singleton, thread-safe e con connection pooling interno), il
/// <see cref="ServiceBusSender"/> della coda, il publisher (lato outbox) e il consumer hosted (lato arricchimento).
/// </summary>
internal static class ServiceBusRegistration
{
    public static IServiceCollection AddServiceBusTransport(this IServiceCollection services)
    {
        // Un solo client per processo (consigliato dall'SDK: gestisce pooling e ricreazione delle connessioni).
        // Auth: connection string (emulatore/locale) oppure namespace + DefaultAzureCredential (managed identity,
        // nessun segreto) in Azure — coerente col principio "no SAS" del resto dell'infra.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
            return !string.IsNullOrWhiteSpace(options.ConnectionString)
                ? new ServiceBusClient(options.ConnectionString)
                : new ServiceBusClient(options.FullyQualifiedNamespace, new DefaultAzureCredential());
        });

        // Sender della coda (singleton: riusabile e thread-safe).
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<ServiceBusClient>();
            var options = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
            return client.CreateSender(options.QueueName);
        });

        // Trasporto = pubblicazione su ASB (sostituisce l'InProcessIntegrationEventPublisher di default).
        services.AddSingleton<IIntegrationEventPublisher, ServiceBusIntegrationEventPublisher>();

        // Consumer disaccoppiato: riceve dalla coda e riusa l'IntegrationEventHandler (scoped, per-messaggio).
        services.AddHostedService<ServiceBusIntegrationEventConsumer>();

        return services;
    }
}
