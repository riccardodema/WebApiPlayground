using System.Text.Json;
using Xunit;

namespace WebApiPlayground.IacTests;

/// <summary>Asserzioni sul modulo Log Analytics (<c>modules/monitoring.bicep</c>).</summary>
public class MonitoringModuleTests
{
    private static JsonElement Template()
    {
        Skip.IfNot(BicepArm.Available, "Bicep CLI non disponibile (installa 'bicep' o 'az bicep').");
        return BicepArm.Compile("modules/monitoring.bicep");
    }

    private static JsonElement Workspace() =>
        BicepArm.Resources(Template(), "Microsoft.OperationalInsights/workspaces").Single();

    [SkippableFact]
    public void Creates_a_log_analytics_workspace_with_deterministic_name()
    {
        // La risorsa esiste...
        Assert.Equal("[variables('workspaceName')]", Workspace().GetProperty("name").GetString());
        // ...e il nome è deterministico (token da uniqueString) e ≤63 char.
        var workspaceName = Template().GetProperty("variables").GetProperty("workspaceName").GetString();
        Assert.StartsWith("[take(", workspaceName);
        Assert.Contains("log-", workspaceName);
        Assert.Contains(", 63)", workspaceName);
    }

    [SkippableFact]
    public void Workspace_uses_pay_per_gb_sku()
    {
        // PerGB2018: nessun costo fisso, si paga solo l'ingestione.
        Assert.Equal("PerGB2018", Workspace().GetProperty("properties").GetProperty("sku").GetProperty("name").GetString());
    }

    [SkippableFact]
    public void Workspace_has_configurable_retention()
    {
        Assert.True(Workspace().GetProperty("properties").TryGetProperty("retentionInDays", out _));
    }
}
