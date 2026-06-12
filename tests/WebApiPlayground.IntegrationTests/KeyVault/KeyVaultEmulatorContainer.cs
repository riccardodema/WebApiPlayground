using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace WebApiPlayground.IntegrationTests.KeyVault;

/// <summary>
/// Emulatore <b>Azure Key Vault</b> (community, <c>james-gould/azure-keyvault-emulator</c>) avviato con
/// Testcontainers "liscio", come SQL/ASB — di proposito SENZA i pacchetti NuGet del progetto emulatore:
/// il loro modulo Testcontainers installerebbe una CA self-signed nel <b>trust store dell'host</b>
/// (invasivo, anche in CI) e aggiungerebbe una dipendenza di terze parti. Qui invece:
/// <list type="bullet">
///   <item>immagine <b>pinnata</b> (supply chain; tag arm nativo su Apple Silicon);</item>
///   <item>certificato TLS generato <b>per-run</b> in una temp dir e montato in <c>/certs</c>
///     (l'immagine si aspetta <c>emulator.pfx</c> con password <c>emulator</c>);</item>
///   <item>trust del self-signed limitato al SOLO <see cref="SecretClient"/> di test (nessuna
///     modifica al trust store).</item>
/// </list>
/// Stessa posture del lato app (<c>KeyVault:Credential = Emulator</c>, solo Development).
/// Vedi <c>docs/keyvault.md</c>.
/// </summary>
public sealed class KeyVaultEmulatorContainer : IAsyncDisposable
{
    private const int EmulatorPort = 4997;

    // Path e password del certificato sono il contratto dell'immagine (ENV Kestrel nel suo Dockerfile).
    private const string CertFileName = "emulator.pfx";
    private const string CertPassword = "emulator";

    private static readonly string Image =
        "docker.io/jamesgoulddev/azure-keyvault-emulator:" +
        (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "3.1.0-arm" : "3.1.0");

    private readonly string _certDirectory;
    private readonly IContainer _container;

    public KeyVaultEmulatorContainer()
    {
        // Cert self-signed per-run: vive solo per la durata del test e non è fidato da nessuno
        // se non dai client costruiti da CreateSecretClient (e dal transport "Emulator" dell'app).
        _certDirectory = Directory.CreateTempSubdirectory("webapiplay-kv-emulator-").FullName;
        WriteSelfSignedCertificate(Path.Combine(_certDirectory, CertFileName));

        _container = new ContainerBuilder(Image)
            .WithPortBinding(EmulatorPort, assignRandomHostPort: true)
            .WithBindMount(_certDirectory, "/certs", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(EmulatorPort))
            .Build();
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => _container.StartAsync(cancellationToken);

    /// <summary>Endpoint https del vault emulato (hostname + porta mappata), da mettere in <c>KeyVault:Uri</c>.</summary>
    public string GetEndpoint() => $"https://{_container.Hostname}:{_container.GetMappedPublicPort(EmulatorPort)}";

    /// <summary>
    /// <see cref="SecretClient"/> per il seed dei secret di test: stesso profilo del lato app in modalità
    /// Emulator — token mintato localmente (l'emulatore non valida firma/claim), challenge verification
    /// disattivata (l'URI non è *.vault.azure.net) e trust del TLS self-signed limitato a QUESTO client.
    /// </summary>
    public SecretClient CreateSecretClient() =>
        new(new Uri(GetEndpoint()), new EmulatorCredential(), new SecretClientOptions
        {
            DisableChallengeResourceVerification = true,
            Transport = new HttpClientTransport(new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            })),
        });

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();

        try
        {
            Directory.Delete(_certDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort: una temp dir orfana non compromette i test successivi (nome univoco per-run).
        }
    }

    private static void WriteSelfSignedCertificate(string pfxPath)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // SAN per gli hostname con cui i client raggiungono il container (host run e rete docker).
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName("host.docker.internal");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        File.WriteAllBytes(pfxPath, certificate.Export(X509ContentType.Pfx, CertPassword));
    }

    /// <summary>
    /// Stesso trucco della <c>KeyVaultEmulatorCredential</c> dell'app (lì internal, qui ridotta al minimo):
    /// JWT ben formato mintato localmente — niente round-trip, l'emulatore non verifica firma né claim.
    /// </summary>
    private sealed class EmulatorCredential : TokenCredential
    {
        private static readonly string Jwt =
            Base64Url("""{"alg":"HS256","typ":"JWT"}""") + "." +
            Base64Url("""{"iss":"https://keyvault-emulator.local","aud":"https://vault.azure.net","exp":253402300799}""") + "." +
            Base64Url("emulator");

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(Jwt, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));

        private static string Base64Url(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
