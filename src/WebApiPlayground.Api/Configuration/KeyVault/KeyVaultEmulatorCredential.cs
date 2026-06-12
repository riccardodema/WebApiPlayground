using System.Text;
using Azure.Core;

namespace WebApiPlayground.Api.Configuration.KeyVault;

/// <summary>
/// <see cref="TokenCredential"/> per l'<b>emulatore</b> Key Vault (SOLO Development; la factory delle
/// credential lo impedisce altrove). L'emulatore accetta qualunque JWT ben formato — non valida firma,
/// issuer, audience né scadenza (<c>SignatureValidator</c> pass-through nel suo JwtBearer) — quindi il
/// token si minta <b>localmente</b>: nessun round-trip HTTP, nessuna dipendenza dai pacchetti client di
/// terze parti dell'emulatore (riduce la superficie supply-chain; vedi <c>docs/keyvault.md</c>).
/// </summary>
internal sealed class KeyVaultEmulatorCredential : TokenCredential
{
    // JWT statico ben formato (header.payload.signature in base64url). I claim sono fittizi e la firma
    // non è verificabile di proposito: serve solo a superare il parsing dell'emulatore.
    private static readonly string Jwt =
        Base64Url("""{"alg":"HS256","typ":"JWT"}""") + "." +
        Base64Url("""{"iss":"https://keyvault-emulator.local","aud":"https://vault.azure.net","exp":253402300799}""") + "." +
        Base64Url("emulator");

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        // Scadenza mobile a ogni richiesta: l'SDK la usa solo per decidere quando richiedere un nuovo token.
        new(Jwt, DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
