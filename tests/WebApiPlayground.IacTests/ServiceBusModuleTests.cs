using System.Text.Json;
using Xunit;

namespace WebApiPlayground.IacTests;

/// <summary>
/// Asserzioni sulla security posture e l'idempotenza del modulo Service Bus
/// (<c>modules/servicebus.bicep</c>): AAD-only (no SAS), RBAC least-privilege
/// (Sender+Receiver sulla coda), naming deterministico, nessun segreto negli output.
/// </summary>
public class ServiceBusModuleTests
{
    // ID dei built-in role del data-plane Service Bus attesi (least privilege: NON Owner).
    private static readonly HashSet<string> ExpectedRoleIds =
    [
        "69a216fc-b8fb-44d8-bc22-1f3c2cd27a39", // Azure Service Bus Data Sender
        "4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0", // Azure Service Bus Data Receiver
    ];

    private static JsonElement Template()
    {
        Skip.IfNot(BicepArm.Available, "Bicep CLI non disponibile (installa 'bicep' o 'az bicep').");
        return BicepArm.Compile("modules/servicebus.bicep");
    }

    private static JsonElement Namespace() =>
        BicepArm.Resources(Template(), "Microsoft.ServiceBus/namespaces").Single();

    private static JsonElement Queue() =>
        BicepArm.Resources(Template(), "Microsoft.ServiceBus/namespaces/queues").Single();

    [SkippableFact]
    public void Local_auth_is_disabled_no_sas()
    {
        // AAD-only: nessuna connection string con SAS (coerente col principio del Key Vault).
        Assert.True(Namespace().GetProperty("properties").GetProperty("disableLocalAuth").GetBoolean());
    }

    [SkippableFact]
    public void Uses_standard_sku_for_queues()
    {
        // Le code richiedono almeno Standard (Basic non supporta i topic; qui usiamo una coda).
        Assert.Equal("Standard", Namespace().GetProperty("sku").GetProperty("name").GetString());
    }

    [SkippableFact]
    public void Queue_dead_letters_poison_messages()
    {
        var props = Queue().GetProperty("properties");
        // Oltre maxDeliveryCount → dead-letter (diagnostica), non scarto silenzioso. In ARM è un'espressione
        // parametro (stringa), quindi verifichiamo che la soglia sia configurata, non il valore puntuale.
        Assert.True(props.TryGetProperty("maxDeliveryCount", out _));
        Assert.True(props.GetProperty("deadLetteringOnMessageExpiration").GetBoolean());
    }

    [SkippableFact]
    public void Namespace_name_is_globally_unique_and_capped_at_50_chars()
    {
        var variables = Template().GetProperty("variables");
        Assert.Contains(
            "uniqueString(subscription().id, resourceGroup().id)",
            variables.GetProperty("nameToken").GetString());

        var name = variables.GetProperty("namespaceName").GetString();
        Assert.StartsWith("[take(", name);
        Assert.Contains(", 50)", name); // limite Azure: 50 char
    }

    [SkippableFact]
    public void Role_assignments_are_conditional_deterministic_and_least_privilege()
    {
        var template = Template();
        var assignments = BicepArm.Resources(template, "Microsoft.Authorization/roleAssignments").ToList();
        Assert.Equal(2, assignments.Count); // Sender + Receiver, niente Owner

        foreach (var assignment in assignments)
        {
            // Condizionali: saltate se il principal è vuoto.
            Assert.StartsWith("[not(empty(", assignment.GetProperty("condition").GetString());
            // Name deterministico via guid() → nessun duplicato a ri-deploy (idempotenza).
            Assert.StartsWith("[guid(", assignment.GetProperty("name").GetString());
        }

        var roleIds = template.GetProperty("variables").GetProperty("roleIds")
            .EnumerateObject().Select(p => p.Value.GetString()!).ToHashSet();
        Assert.Equal(ExpectedRoleIds, roleIds);
    }

    [SkippableFact]
    public void Outputs_expose_no_secrets()
    {
        // Solo identificatori + FQDN: nessuna connection string (l'app usa managed identity).
        var outputs = Template().GetProperty("outputs").EnumerateObject().Select(o => o.Name).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "namespaceName", "fullyQualifiedNamespace", "queueName", "id" }, outputs);
    }
}
