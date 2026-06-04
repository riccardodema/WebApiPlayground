using Microsoft.AspNetCore.Authentication;
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
    ///
    /// <para>Pattern <i>disabled-until-configured</i>: se <c>AzureAd:ClientId</c> non è impostato
    /// (es. app registration non ancora creata), in <b>Development</b> si registra uno schema di
    /// sviluppo (<see cref="DevelopmentAuthHandler"/>) per poter provare l'API da Scalar senza un
    /// tenant reale; fuori da Development si fallisce subito (mai accesso anonimo silenzioso in prod).
    /// Senza questo gate, <c>AddMicrosoftIdentityWebApi</c> con ClientId vuoto lancerebbe
    /// <c>IDW10106</c> alla prima richiesta — anche su <c>/scalar/v1</c>. Vedi <c>.claude/lessons.md</c> [L12].</para>
    /// </summary>
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var azureAd = configuration.GetSection("AzureAd");

        if (!string.IsNullOrWhiteSpace(azureAd["ClientId"]))
        {
            services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(azureAd);

            return services;
        }

        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "Autenticazione non configurata: impostare 'AzureAd:ClientId' (più TenantId/Audience) " +
                "per abilitare Microsoft Entra ID. Il bypass è consentito solo in ambiente Development.");

        Serilog.Log.Warning(
            "AzureAd non configurato: autenticazione in modalità BYPASS di sviluppo (ogni richiesta " +
            "è autenticata con scope Books pieni). Configurare AzureAd per abilitare Entra ID reale.");

        services
            .AddAuthentication(DevelopmentAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentAuthHandler.SchemeName, _ => { });

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
