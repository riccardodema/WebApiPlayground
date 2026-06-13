using WebApiPlayground.Infrastructure.Outbox.ServiceBus;
using Xunit;

namespace WebApiPlayground.Tests.Outbox;

/// <summary>
/// La decisione di trasporto della composition root passa da <see cref="ServiceBusOptions.IsConfigured"/>:
/// la matrice completa (connection string / namespace / entrambi / nessuno / whitespace) e i default
/// che devono combaciare con l'IaC (nome coda) e con la semantica FIFO (concorrenza 1).
/// </summary>
public class ServiceBusOptionsTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("   ", "   ", false)]                       // whitespace NON è configurazione
    [InlineData("Endpoint=sb://local;...", null, true)]     // emulatore/locale: connection string
    [InlineData(null, "sb-x.servicebus.windows.net", true)] // Azure: namespace + managed identity
    [InlineData("Endpoint=sb://local;...", "sb-x.servicebus.windows.net", true)]
    public void IsConfigured_requires_at_least_one_endpoint(string? connectionString, string? fqns, bool expected)
    {
        var options = new ServiceBusOptions { ConnectionString = connectionString, FullyQualifiedNamespace = fqns };

        Assert.Equal(expected, options.IsConfigured);
    }

    [Fact]
    public void Queue_name_default_matches_the_iac_queue()
    {
        // Lo stesso nome vive in infra/main.bicep (serviceBusQueueName) e nel Config.json dell'emulatore:
        // cambiarlo solo qui romperebbe il binding silenziosamente.
        Assert.Equal("popularity-enrichment", new ServiceBusOptions().QueueName);
    }

    [Fact]
    public void Concurrency_default_is_one_to_preserve_ordering()
    {
        Assert.Equal(1, new ServiceBusOptions().MaxConcurrentCalls);
    }
}
