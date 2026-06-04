using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Api.Extensions;
using Xunit;

namespace WebApiPlayground.Tests.Authorization;

/// <summary>
/// Gate "disabled-until-configured" di <c>AddApiAuthentication</c>: con <c>AzureAd:ClientId</c>
/// assente l'app deve restare avviabile in Development (schema di sviluppo) e fallire subito
/// altrove; con il ClientId configurato registra il JWT Bearer di Entra. Vedi <c>.claude/lessons.md</c> [L12].
/// </summary>
public class AuthenticationExtensionsTests
{
    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static async Task<string?> DefaultSchemeNameAsync(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = await schemes.GetDefaultAuthenticateSchemeAsync();
        return scheme?.Name;
    }

    [Fact]
    public async Task WhenClientIdMissing_InDevelopment_RegistersDevelopmentScheme()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddApiAuthentication(Config(), new FakeEnvironment { EnvironmentName = Environments.Development });

        Assert.Equal(DevelopmentAuthHandler.SchemeName, await DefaultSchemeNameAsync(services));
    }

    [Fact]
    public void WhenClientIdMissing_OutsideDevelopment_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddApiAuthentication(Config(), new FakeEnvironment { EnvironmentName = Environments.Production }));

        Assert.Contains("AzureAd:ClientId", ex.Message);
    }

    [Fact]
    public async Task WhenClientIdConfigured_RegistersJwtBearer()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddApiAuthentication(
            Config(
                ("AzureAd:Instance", "https://login.microsoftonline.com/"),
                ("AzureAd:TenantId", "00000000-0000-0000-0000-000000000000"),
                ("AzureAd:ClientId", "11111111-1111-1111-1111-111111111111")),
            new FakeEnvironment { EnvironmentName = Environments.Production });

        // Anche in Production: ClientId presente ⇒ Entra reale (JwtBearerDefaults.AuthenticationScheme).
        Assert.Equal("Bearer", await DefaultSchemeNameAsync(services));
    }
}
