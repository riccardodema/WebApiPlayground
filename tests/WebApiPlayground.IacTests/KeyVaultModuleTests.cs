using System.Text.Json;
using Xunit;

namespace WebApiPlayground.IacTests;

/// <summary>
/// Asserzioni sulla security posture e l'idempotenza del modulo Key Vault
/// (<c>modules/keyvault.bicep</c>). Complementari a PSRule: qui verifichiamo le
/// scelte SPECIFICHE di questo progetto.
/// </summary>
public class KeyVaultModuleTests
{
    // ID dei built-in role attesi (Key Vault).
    private static readonly HashSet<string> ExpectedRoleIds =
    [
        "b86a8fe4-44ce-4948-aee5-eccb2c155cd6", // Key Vault Secrets Officer
        "4633458b-17de-408a-b874-0445c86b69e6", // Key Vault Secrets User
    ];

    private static JsonElement Template()
    {
        Skip.IfNot(BicepArm.Available, "Bicep CLI non disponibile (installa 'bicep' o 'az bicep').");
        return BicepArm.Compile("modules/keyvault.bicep");
    }

    private static JsonElement Vault() =>
        BicepArm.Resources(Template(), "Microsoft.KeyVault/vaults").Single();

    private static JsonElement VaultProperties() => Vault().GetProperty("properties");

    [SkippableFact]
    public void Uses_rbac_authorization_not_access_policies()
    {
        var props = VaultProperties();
        Assert.True(props.GetProperty("enableRbacAuthorization").GetBoolean());
        Assert.False(props.TryGetProperty("accessPolicies", out _), "niente access policies legacy");
    }

    [SkippableFact]
    public void Soft_delete_is_enabled_with_90_day_retention()
    {
        var props = VaultProperties();
        Assert.True(props.GetProperty("enableSoftDelete").GetBoolean());
        Assert.Equal(90, props.GetProperty("softDeleteRetentionInDays").GetInt32());
    }

    [SkippableFact]
    public void Purge_protection_is_never_hardcoded_false()
    {
        // Deve restare un'espressione condizionale: ARM vieta di disabilitare la
        // purge protection una volta attiva, quindi il letterale `false` è un bug.
        var purge = VaultProperties().GetProperty("enablePurgeProtection");
        Assert.Equal(JsonValueKind.String, purge.ValueKind);
        Assert.Contains("if(parameters('enablePurgeProtection')", purge.GetString());
    }

    [SkippableFact]
    public void Network_acls_are_default_deny_with_azure_services_bypass()
    {
        var acls = VaultProperties().GetProperty("networkAcls");
        Assert.Equal("Deny", acls.GetProperty("defaultAction").GetString());
        Assert.Equal("AzureServices", acls.GetProperty("bypass").GetString());
    }

    [SkippableFact]
    public void Key_vault_name_is_globally_unique_and_capped_at_24_chars()
    {
        var variables = Template().GetProperty("variables");
        Assert.Contains(
            "uniqueString(subscription().id, resourceGroup().id)",
            variables.GetProperty("nameToken").GetString());

        var name = variables.GetProperty("keyVaultName").GetString();
        Assert.StartsWith("[take(", name);
        Assert.Contains(", 24)", name); // limite Azure: 24 char
    }

    [SkippableFact]
    public void No_secret_values_are_created_by_the_iac()
    {
        // L'IaC crea solo il vault: i valori dei secret si impostano fuori
        // (nessun segreto transita per i deployment ARM).
        Assert.Empty(BicepArm.Resources(Template(), "Microsoft.KeyVault/vaults/secrets"));
    }

    [SkippableFact]
    public void Outputs_expose_no_secrets()
    {
        var outputs = Template().GetProperty("outputs").EnumerateObject().Select(o => o.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "name", "uri", "id" }, outputs);
    }

    [SkippableFact]
    public void Role_assignments_are_conditional_and_deterministic()
    {
        var template = Template();
        var assignments = BicepArm.Resources(template, "Microsoft.Authorization/roleAssignments").ToList();
        Assert.Equal(2, assignments.Count);

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
}
