using System.Net;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Xunit;

namespace WebApiPlayground.DockerTests;

/// <summary>
/// Smoke test LIVE: builda l'immagine dal <c>Dockerfile</c> di root, avvia il container in
/// Development e verifica che <c>/health/live</c> risponda <c>200</c>. Copre <b>build + runtime</b>
/// end-to-end. È la liveness: il processo parte anche senza DB (l'<c>OutboxDispatcher</c> isola gli
/// errori di batch). Richiede Docker → <see cref="SkippableFactAttribute"/> salta se il daemon è assente
/// (come IacTests salta senza la Bicep CLI).
/// </summary>
public class ContainerSmokeTests
{
    private const ushort HttpPort = 8080;

    [SkippableFact]
    public async Task Container_starts_and_serves_health_live()
    {
        Skip.IfNot(await DockerAvailableAsync(), "Docker non disponibile: smoke test live saltato.");

        IFutureDockerImage image = new ImageFromDockerfileBuilder()
            // Build context = cartella della solution (root del repo), Dockerfile = ./Dockerfile.
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
            .WithDockerfile("Dockerfile")
            .WithName($"webapiplayground-smoketest:{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();

        await image.CreateAsync();

        var container = new ContainerBuilder()
            .WithImage(image)
            // Development: niente fail-fast config, auth BYPASS, nessun HTTPS redirect (così /health/live
            // risponde su HTTP). Nessuna connection string: la liveness non tocca il DB.
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", HttpPort.ToString())
            .WithPortBinding(HttpPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(HttpPort).ForPath("/health/live")))
            .WithCleanUp(true)
            .Build();

        try
        {
            await container.StartAsync();

            using var http = new HttpClient
            {
                BaseAddress = new UriBuilder(
                    Uri.UriSchemeHttp, container.Hostname, container.GetMappedPublicPort(HttpPort)).Uri,
            };

            var response = await http.GetAsync("/health/live");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>True se il daemon Docker risponde: altrimenti lo smoke test si SKIPpa.</summary>
    private static async Task<bool> DockerAvailableAsync()
    {
        try
        {
            using var client = new DockerClientConfiguration().CreateClient();
            await client.System.PingAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
