using System.Text.Json;
using Xunit;

namespace WebApiPlayground.IacTests;

/// <summary>Asserzioni sull'orchestrazione a livello di subscription (<c>main.bicep</c>).</summary>
public class MainTemplateTests
{
    private static JsonElement Template()
    {
        Skip.IfNot(BicepArm.Available, "Bicep CLI non disponibile (installa 'bicep' o 'az bicep').");
        return BicepArm.Compile("main.bicep");
    }

    [SkippableFact]
    public void Main_targets_subscription_scope()
    {
        var schema = Template().GetProperty("$schema").GetString();
        Assert.Contains("subscriptionDeploymentTemplate", schema);
    }

    [SkippableFact]
    public void Main_creates_exactly_one_resource_group()
    {
        var groups = BicepArm.Resources(Template(), "Microsoft.Resources/resourceGroups").ToList();
        Assert.Single(groups);
        Assert.Contains("rg-", groups[0].GetProperty("name").GetString());
    }

    [SkippableFact]
    public void Main_deploys_the_key_vault_module()
    {
        var deployments = BicepArm.Resources(Template(), "Microsoft.Resources/deployments");
        Assert.Contains(deployments, d => d.GetProperty("name").GetString() == "keyvault");
    }

    [SkippableFact]
    public void Main_provisions_monitoring_when_enabled()
    {
        var monitoring = BicepArm.Resources(Template(), "Microsoft.Resources/deployments")
            .Single(d => d.GetProperty("name").GetString() == "monitoring");
        // Condizionale sul flag enableMonitoring (toggle per azzerare i costi).
        Assert.Contains("parameters('enableMonitoring')", monitoring.GetProperty("condition").GetString());
    }
}
