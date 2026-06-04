using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebApiPlayground.Api.ErrorHandling;
using WebApiPlayground.Api.RateLimiting;

namespace WebApiPlayground.Api.Extensions;

/// <summary>
/// Registra il rate limiter nativo .NET (<c>AddRateLimiter</c>/<c>UseRateLimiter</c>)
/// con due policy sliding-window — <c>read</c> e <c>write</c> — partizionate per client
/// (<see cref="ClientPartition"/>). Le richieste oltre il limite sono respinte con <b>429</b> e un
/// corpo <c>application/problem+json</c> (RFC 7807) coerente con gli altri errori dell'API
/// (stesso <c>correlationId</c>/<c>traceId</c> via <see cref="ProblemDetailsEnricher"/>), più
/// l'header <c>Retry-After</c>. Vedi <c>.claude/context/rate-limiting.md</c>.
/// </summary>
public static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(RateLimitingOptions.PolicyNames.Read,
                context => SlidingWindow(context, options => options.Read));
            limiter.AddPolicy(RateLimitingOptions.PolicyNames.Write,
                context => SlidingWindow(context, options => options.Write));

            limiter.OnRejected = WriteRateLimitProblemAsync;
        });

        return services;
    }

    // Le opzioni si leggono a tempo di richiesta (IOptions), non alla registrazione: così riflettono
    // l'effettiva configurazione bindata (la sezione si lega lazy, post-build), niente cattura eager.
    private static RateLimitPartition<string> SlidingWindow(
        HttpContext context, Func<RateLimitingOptions, SlidingWindowPolicyOptions> selectPolicy)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        var policy = selectPolicy(options);

        return RateLimitPartition.GetSlidingWindowLimiter(
            ClientPartition.ResolveKey(context),
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = TimeSpan.FromSeconds(policy.WindowSeconds),
                SegmentsPerWindow = policy.SegmentsPerWindow,
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
    }

    private static async ValueTask WriteRateLimitProblemAsync(
        OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;

        // Retry-After: quanti secondi prima di ritentare. Standard HTTP per il 429, così il client
        // sa quando ritentare (anziché ritentare subito e venire respinto di nuovo).
        httpContext.Response.Headers.RetryAfter =
            ResolveRetryAfterSeconds(context).ToString(CultureInfo.InvariantCulture);

        // Stesso canale ProblemDetails dell'idempotency (IdempotencyMiddleware.WriteProblemAsync):
        // arricchito con correlationId/traceId e servito come application/problem+json.
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests.",
            Detail = "Rate limit exceeded. Retry after the period indicated by the 'Retry-After' header.",
            Type = "https://datatracker.ietf.org/doc/html/rfc6585#section-4",
            Instance = httpContext.Request.Path,
        };
        ProblemDetailsEnricher.Enrich(httpContext, problem);

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await httpContext.Response.WriteAsJsonAsync(
            problem, problem.GetType(), options: null, contentType: "application/problem+json", cancellationToken);
    }

    /// <summary>
    /// Secondi da attendere prima di ritentare: il metadata nativo del lease se presente, altrimenti
    /// la finestra della policy che ha respinto (alcuni limiter non emettono <c>RetryAfter</c>).
    /// </summary>
    private static int ResolveRetryAfterSeconds(OnRejectedContext context)
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            return (int)Math.Ceiling(retryAfter.TotalSeconds);

        var policyName = context.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        return policyName switch
        {
            RateLimitingOptions.PolicyNames.Write => options.Write.WindowSeconds,
            _ => options.Read.WindowSeconds,
        };
    }
}
