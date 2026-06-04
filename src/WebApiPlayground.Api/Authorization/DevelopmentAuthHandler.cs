using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace WebApiPlayground.Api.Authorization;

/// <summary>
/// Schema di autenticazione di <b>sviluppo</b>, usato SOLO quando Entra ID non è configurato
/// (sezione <c>AzureAd</c> vuota) e l'ambiente è Development. Autentica ogni richiesta come
/// <c>dev-user</c> con lo scope di scrittura (claim <c>scp</c> = <see cref="BooksPermissions.ScopeReadWrite"/>),
/// che soddisfa sia la policy di lettura sia quella di scrittura: così si può provare l'API da
/// Scalar senza un tenant Entra reale. Mai registrato fuori da Development — vedi
/// <c>AuthenticationExtensions.AddApiAuthentication</c> e <c>.claude/context/auth.md</c>.
/// </summary>
public sealed class DevelopmentAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Development";

    public DevelopmentAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "dev-user"),
            new Claim("scp", BooksPermissions.ScopeReadWrite),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
