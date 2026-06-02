using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using WebApiPlayground.Api.Authorization;

namespace WebApiPlayground.Api.Extensions;

/// <summary>
/// Registrazione di autenticazione (Entra ID / JWT Bearer) e autorizzazione
/// (policy scope-or-app-permission), seguendo il pattern <c>AddApplication</c>/<c>AddInfrastructure</c>.
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Valida i bearer token emessi da Microsoft Entra ID. La configurazione è letta
    /// dalla sezione <c>AzureAd</c> (Instance, TenantId, ClientId/Audience). Nessun segreto:
    /// la validazione usa le chiavi pubbliche del tenant.
    /// </summary>
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"));

        return services;
    }

    /// <summary>
    /// Registra le policy di autorizzazione. Ogni policy accetta sia uno scope delegato
    /// (claim <c>scp</c>, flusso utente→API) sia una app permission (claim <c>roles</c>,
    /// flusso macchina→macchina): vedi <see cref="BooksPermissions"/>.
    /// </summary>
    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        // Registra l'handler per ScopeOrAppPermissionAuthorizationRequirement.
        services.AddRequiredScopeOrAppPermissionAuthorization();

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.ReadBooks, policy =>
                policy.RequireScopeOrAppPermission(
                    BooksPermissions.ReadScopes,
                    BooksPermissions.ReadAppPermissions))
            .AddPolicy(AuthorizationPolicies.WriteBooks, policy =>
                policy.RequireScopeOrAppPermission(
                    BooksPermissions.WriteScopes,
                    BooksPermissions.WriteAppPermissions));

        return services;
    }
}
