using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Auth;

/// <summary>
/// Factory che NON sostituisce l'autenticazione: l'app gira con la pipeline <c>JwtBearer</c> REALE
/// (registrata da <c>AddMicrosoftIdentityWebApi</c>), puntata su una <see cref="FakeOidcAuthority"/>
/// in-proc. Due soli adattamenti, dichiarati: metadata su http loopback
/// (<c>RequireHttpsMetadata=false</c>) e issuer validato col confronto standard invece che con
/// l'<c>AadIssuerValidator</c> (che richiede il metadata Entra reale). Firma (via JWKS), audience,
/// lifetime e policy di scope/ruolo sono quelle di produzione.
/// </summary>
public sealed class RealJwtApiFactory : PlaygroundApiFactory, IAsyncLifetime
{
    public FakeOidcAuthority Authority { get; private set; } = null!;

    protected override bool UseTestAuthentication => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting, non ConfigureAppConfiguration: la sezione AzureAd decide il ramo di
        // autenticazione in FASE BUILDER (gate disabled-until-configured) — vedi [L25].
        builder.UseSetting("AzureAd:Instance", Authority.BaseUrl + "/");
        builder.UseSetting("AzureAd:TenantId", Authority.TenantId);
        builder.UseSetting("AzureAd:ClientId", FakeOidcAuthority.ClientId);
        builder.UseSetting("AzureAd:Audience", FakeOidcAuthority.Audience);

        // Configure (non PostConfigure): il PostConfigure del framework valida RequireHttpsMetadata e
        // gira PRIMA di qualunque nostro PostConfigure registrato dopo — il flag va spento nello stage
        // Configure, che segue comunque quello di Microsoft.Identity.Web (ordine di registrazione).
        builder.ConfigureServices(services =>
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.RequireHttpsMetadata = false; // authority finta su http loopback
                options.TokenValidationParameters.IssuerValidator = null;
                options.TokenValidationParameters.ValidIssuer = Authority.Issuer;
            }));

        base.ConfigureWebHost(builder);
    }

    // L'authority deve esistere PRIMA che l'host si costruisca (ConfigureWebHost ne legge gli URL).
    async Task IAsyncLifetime.InitializeAsync()
    {
        Authority = await FakeOidcAuthority.StartAsync();
        await base.InitializeAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await Authority.DisposeAsync();
    }
}

/// <summary>
/// Matrice ESAUSTIVA della validazione token sulla pipeline Bearer reale: il resto della suite usa
/// il <c>TestAuthHandler</c> (veloce, claim simulati), quindi senza questi test firma/issuer/audience/
/// lifetime non sarebbero MAI esercitati — si scoprirebbero solo col tenant vero in produzione.
/// </summary>
[Collection("Integration")]
public class RealJwtAuthTests : IClassFixture<RealJwtApiFactory>
{
    private readonly RealJwtApiFactory _factory;
    private readonly HttpClient _client;

    public RealJwtAuthTests(RealJwtApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> GetBooksAsync(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/books");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    // ---- Percorsi felici (token validi emessi dall'authority) -----------------

    [Fact]
    public async Task Valid_token_with_read_scope_reaches_the_api()
    {
        // Passa: metadata fetch → JWKS → firma RSA verificata → issuer/audience/lifetime ok → policy scp.
        var response = await GetBooksAsync(_factory.Authority.CreateToken(scope: BooksPermissions.ScopeRead));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Valid_app_permission_roles_claim_reaches_the_api()
    {
        // Flusso macchina→macchina: claim roles (app permission) al posto dello scope delegato.
        var token = _factory.Authority.CreateToken(roles: [BooksPermissions.AppRead]);

        var response = await GetBooksAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Valid_write_scope_allows_writes()
    {
        var token = _factory.Authority.CreateToken(scope: BooksPermissions.ScopeReadWrite);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/books")
        {
            Content = JsonContent.Create(new CreateBookDto("JWT Probe", await SeedAuthorAsync())),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- 401: il token non supera la VALIDAZIONE ------------------------------

    [Fact]
    public async Task Missing_token_is_challenged_with_bearer()
    {
        var response = await GetBooksAsync(token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        // Oltre il ClockSkew di default (5 minuti): scaduto da 30.
        var token = _factory.Authority.CreateToken(
            scope: BooksPermissions.ScopeRead,
            notBefore: DateTime.UtcNow.AddHours(-1),
            expires: DateTime.UtcNow.AddMinutes(-30));

        var response = await GetBooksAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Token_not_yet_valid_is_rejected()
    {
        var token = _factory.Authority.CreateToken(
            scope: BooksPermissions.ScopeRead,
            notBefore: DateTime.UtcNow.AddMinutes(30),
            expires: DateTime.UtcNow.AddHours(1));

        var response = await GetBooksAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        var token = _factory.Authority.CreateToken(
            scope: BooksPermissions.ScopeRead, audience: "api://someone-elses-api");

        var response = await GetBooksAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_issuer_is_rejected()
    {
        var token = _factory.Authority.CreateToken(
            scope: BooksPermissions.ScopeRead, issuer: "https://evil.example/v2.0");

        var response = await GetBooksAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_an_unknown_key_is_rejected()
    {
        // Stessi claim di un token valido ma firmato con una chiave fuori dal JWKS: la firma
        // non è verificabile → 401. È IL test che il TestAuthHandler non potrà mai darti.
        var token = _factory.Authority.CreateToken(scope: BooksPermissions.ScopeRead, signWithForeignKey: true);

        var response = await GetBooksAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Garbage_token_is_rejected()
    {
        var response = await GetBooksAsync("not-a-jwt-at-all");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 403: token AUTENTICO ma senza i permessi richiesti --------------------

    [Fact]
    public async Task Token_with_neither_scp_nor_roles_is_rejected_at_authentication()
    {
        // Comportamento REALE di Microsoft.Identity.Web (scoperto da questo test): un access token
        // senza né scp né roles è rifiutato già in AUTENTICAZIONE (401), non in autorizzazione —
        // MIW pretende che un token per una web API porti almeno uno dei due claim. Il 403 "vero"
        // (autenticato ma senza il permesso giusto) è coperto da Read_scope_cannot_write.
        var token = _factory.Authority.CreateToken(); // né scp né roles

        var response = await GetBooksAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_scope_cannot_write()
    {
        var token = _factory.Authority.CreateToken(scope: BooksPermissions.ScopeRead);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/books")
        {
            Content = JsonContent.Create(new CreateBookDto("Should Not Exist", 1)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<int> SeedAuthorAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebApiPlayground.Infrastructure.Persistence.PlaygroundDbContext>();
        var author = new WebApiPlayground.Domain.Entities.Author { FullName = "Frank Herbert" };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        return author.Id;
    }
}
