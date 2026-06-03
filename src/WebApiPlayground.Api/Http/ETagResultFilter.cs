using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;

namespace WebApiPlayground.Api.Http;

/// <summary>
/// HTTP caching sui GET tramite <b>ETag</b> + <b>Cache-Control</b> (conditional requests, RFC 9110).
///
/// <para>Per ogni <c>GET</c> che produce un <see cref="ObjectResult"/> 200 con body:
/// calcola l'ETag dalla rappresentazione, lo mette in <c>ETag</c> + <c>Cache-Control</c>; se la
/// richiesta porta un <c>If-None-Match</c> che combacia, sostituisce il body con
/// <c>304 Not Modified</c> (niente payload) — il client riusa la copia che ha già, risparmiando
/// banda e (ri)serializzazione.</para>
///
/// <para>Si combina con il caching server-side (HybridCache): quello taglia DB/CPU, questo taglia
/// la rete. <c>Cache-Control: private, no-cache</c> perché gli endpoint sono autenticati (mai cache
/// condivise/proxy) e <c>no-cache</c> impone la rivalidazione, mettendo in mostra il 304.</para>
/// </summary>
public sealed class ETagResultFilter : IAsyncResultFilter
{
    private const string CacheControlValue = "private, no-cache";

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (IsCacheableGet(context, out var value))
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
            var etag = ETag.Compute(payload);

            var response = context.HttpContext.Response;
            response.Headers.ETag = etag;
            response.Headers.CacheControl = CacheControlValue;

            if (IfNoneMatchSatisfied(context.HttpContext.Request, etag))
            {
                // Risorsa invariata: niente body. Gli header ETag/Cache-Control restano sulla Response.
                context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
            }
        }

        await next();
    }

    private static bool IsCacheableGet(ResultExecutingContext context, out object value)
    {
        value = null!;
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method))
            return false;

        // Solo risposte 200 con un corpo (Ok(dto) ⇒ OkObjectResult con StatusCode 200).
        if (context.Result is ObjectResult { Value: { } body, StatusCode: null or StatusCodes.Status200OK })
        {
            value = body;
            return true;
        }

        return false;
    }

    private static bool IfNoneMatchSatisfied(HttpRequest request, string etag)
    {
        var ifNoneMatch = request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Count == 0)
            return false;

        // Match su "*" (qualunque rappresentazione) oppure su un ETag uguale a quello corrente.
        return ifNoneMatch.Any(candidate =>
            candidate.Equals(EntityTagHeaderValue.Any) ||
            string.Equals(candidate.Tag.Value, etag, StringComparison.Ordinal));
    }
}
