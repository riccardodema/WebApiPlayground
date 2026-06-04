using Microsoft.Extensions.Configuration;
using WebApiPlayground.Api.RateLimiting;
using Xunit;

namespace WebApiPlayground.Tests.RateLimiting;

/// <summary>
/// I default sono "sensati" (letture più generose delle scritture, niente accodamento) e la sezione
/// <c>RateLimiting</c> si lega correttamente — lo stesso percorso usato da <c>AddApiRateLimiting</c>.
/// </summary>
public class RateLimitingOptionsTests
{
    [Fact]
    public void Defaults_AreSensible_ReadMoreGenerousThanWrite()
    {
        var options = new RateLimitingOptions();

        Assert.Equal(100, options.Read.PermitLimit);
        Assert.Equal(20, options.Write.PermitLimit);
        Assert.True(options.Read.PermitLimit > options.Write.PermitLimit,
            "Le letture devono avere un limite più generoso delle scritture.");

        // QueueLimit 0 → niente accodamento, rifiuto immediato con 429.
        Assert.Equal(0, options.Read.QueueLimit);
        Assert.Equal(0, options.Write.QueueLimit);
    }

    [Fact]
    public void Binds_FromConfigurationSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Read:PermitLimit"] = "7",
                ["RateLimiting:Read:WindowSeconds"] = "30",
                ["RateLimiting:Write:PermitLimit"] = "3",
                ["RateLimiting:Write:SegmentsPerWindow"] = "2",
                ["RateLimiting:Write:QueueLimit"] = "1",
            })
            .Build();

        var options = config.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>();

        Assert.NotNull(options);
        Assert.Equal(7, options!.Read.PermitLimit);
        Assert.Equal(30, options.Read.WindowSeconds);
        Assert.Equal(3, options.Write.PermitLimit);
        Assert.Equal(2, options.Write.SegmentsPerWindow);
        Assert.Equal(1, options.Write.QueueLimit);

        // Le chiavi non specificate mantengono il default.
        Assert.Equal(60, options.Write.WindowSeconds);
    }
}
