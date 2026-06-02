using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebApiPlayground.IntegrationTests.Infrastructure;

/// <summary>
/// Schema di autenticazione fittizio per i test: sostituisce il JWT Bearer Entra ID così
/// le prove non richiedono un tenant reale. Costruisce il <see cref="ClaimsPrincipal"/> dai
/// claim simulati passati via header:
/// <list type="bullet">
/// <item><c>X-Test-Scope</c> → claim <c>scp</c> (token delegato, utente→API);</item>
/// <item><c>X-Test-Roles</c> → claim <c>roles</c> (app permission, macchina→macchina).</item>
/// </list>
/// Senza alcun header non autentica (→ 401), così si può testare l'assenza di token.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string ScopeHeader = "X-Test-Scope";
    public const string RolesHeader = "X-Test-Roles";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var hasScope = Request.Headers.TryGetValue(ScopeHeader, out var scope);
        var hasRoles = Request.Headers.TryGetValue(RolesHeader, out var roles);

        if (!hasScope && !hasRoles)
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-user") };

        // claim "scp": gli scope sono separati da spazio in un singolo claim (come nei token reali)
        if (hasScope && !string.IsNullOrWhiteSpace(scope))
            claims.Add(new Claim("scp", scope.ToString().Replace(',', ' ')));

        // claim "roles": una entry per ruolo/app-permission
        if (hasRoles)
            foreach (var role in roles.ToString()
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                claims.Add(new Claim("roles", role));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
