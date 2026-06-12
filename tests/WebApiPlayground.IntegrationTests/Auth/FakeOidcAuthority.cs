using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace WebApiPlayground.IntegrationTests.Auth;

/// <summary>
/// <b>Authority OIDC finta</b>, self-hosted in-proc su loopback (Kestrel, porta effimera, http):
/// espone il discovery document e il JWKS con una chiave RSA generata per-run, e minta token
/// firmati con quella chiave (o con una chiave ESTRANEA, per i test negativi). Serve a esercitare
/// la pipeline <c>JwtBearer</c>/Entra REALE dell'app — fetch del metadata, risoluzione della chiave
/// dal JWKS, validazione di firma/issuer/audience/lifetime — senza un tenant Entra né dipendenze
/// di terze parti. Mai usata fuori dai test.
/// </summary>
public sealed class FakeOidcAuthority : IAsyncDisposable
{
    /// <summary>ClientId finto dell'app registration (basta che sia non-vuoto e stabile).</summary>
    public const string ClientId = "11111111-2222-3333-4444-555555555555";

    /// <summary>Audience attesa dall'API (va in <c>AzureAd:Audience</c> e nei token validi).</summary>
    public const string Audience = "api://webapiplayground-tests";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _keyId = Guid.NewGuid().ToString("N");
    private WebApplication? _app;

    public string TenantId { get; } = Guid.NewGuid().ToString();

    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Issuer dei token (lo stesso del discovery document), formato v2.0 di Entra.</summary>
    public string Issuer => $"{BaseUrl}/{TenantId}/v2.0";

    private FakeOidcAuthority()
    {
    }

    public static async Task<FakeOidcAuthority> StartAsync()
    {
        var authority = new FakeOidcAuthority();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // niente rumore nei log dei test
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.MapGet("/{tenant}/v2.0/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = authority.Issuer,
            jwks_uri = $"{authority.BaseUrl}/{authority.TenantId}/discovery/v2.0/keys",
            authorization_endpoint = $"{authority.BaseUrl}/{authority.TenantId}/oauth2/v2.0/authorize",
            token_endpoint = $"{authority.BaseUrl}/{authority.TenantId}/oauth2/v2.0/token",
            response_modes_supported = new[] { "query" },
            response_types_supported = new[] { "code" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            subject_types_supported = new[] { "pairwise" },
        }));

        app.MapGet("/{tenant}/discovery/v2.0/keys", () =>
        {
            var parameters = authority._rsa.ExportParameters(includePrivateParameters: false);
            return Results.Json(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        kid = authority._keyId,
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent),
                    },
                },
            });
        });

        await app.StartAsync();

        authority._app = app;
        authority.BaseUrl = app.Urls.First().TrimEnd('/');
        return authority;
    }

    /// <summary>
    /// Minta un access token. I default producono un token VALIDO; ogni parametro permette di
    /// rompere esattamente una proprietà (issuer, audience, lifetime, firma) per i test negativi.
    /// </summary>
    public string CreateToken(
        string? scope = null,
        string[]? roles = null,
        string? issuer = null,
        string? audience = null,
        DateTime? notBefore = null,
        DateTime? expires = null,
        bool signWithForeignKey = false)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "test-user"),
            new("oid", Guid.NewGuid().ToString()),
            new("tid", TenantId),
        };
        if (scope is not null)
            claims.Add(new Claim("scp", scope));
        foreach (var role in roles ?? [])
            claims.Add(new Claim("roles", role));

        SigningCredentials credentials;
        if (signWithForeignKey)
        {
            // Chiave ESTRANEA al JWKS pubblicato (kid diverso): firma non verificabile → 401.
            using var foreignRsa = RSA.Create(2048);
            var foreignKey = new RsaSecurityKey(foreignRsa.ExportParameters(true)) { KeyId = Guid.NewGuid().ToString("N") };
            credentials = new SigningCredentials(foreignKey, SecurityAlgorithms.RsaSha256);
        }
        else
        {
            credentials = new SigningCredentials(
                new RsaSecurityKey(_rsa) { KeyId = _keyId }, SecurityAlgorithms.RsaSha256);
        }

        var token = new JwtSecurityToken(
            issuer: issuer ?? Issuer,
            audience: audience ?? Audience,
            claims: claims,
            // NB: il validatore applica un ClockSkew di default di 5 minuti — i test di lifetime
            // devono sforare di PIÙ (qui si usano default ampiamente fuori finestra).
            notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            expires: expires ?? DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _rsa.Dispose();
    }
}
