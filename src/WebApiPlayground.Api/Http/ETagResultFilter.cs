using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;
using WebApiPlayground.Application.Concurrency;

namespace WebApiPlayground.Api.Http;

/// <summary>
/// Emette l'<b>ETag</b> (RFC 9110) delle risposte e gestisce le richieste condizionali. Due sorgenti
/// di ETag, a seconda del body:
///
/// <list type="bullet">
/// <item><b>Risorsa versionata</b> (<see cref="IVersionedResource"/> con <c>Version</c> valorizzato —
/// es. il singolo libro): l'ETag è il <b>token di versione</b> (la rowversion in base64). Lo stesso
/// header serve sia il caching condizionale (<c>304</c>) sia l'optimistic concurrency (il client lo
/// rimanda in <c>If-Match</c> sulle scritture → 412/428). Emesso anche sulle risposte di scrittura
/// (PUT 200 / POST 201), così il client conosce la nuova versione senza una GET intermedia.</item>
/// <item><b>Altre risposte GET</b> (es. liste paginate): l'ETag è l'<b>impronta della
/// rappresentazione</b> (hash) — solo caching condizionale, nessuna semantica di concorrenza.</item>
/// </list>
///
/// <para><c>Cache-Control: private, no-cache</c> sui GET (endpoint autenticati: mai cache condivise;
/// <c>no-cache</c> = rivalida sempre, mettendo in mostra il 304).</para>
/// </summary>
public sealed class ETagResultFilter : IAsyncResultFilter
{
    private const string CacheControlValue = "private, no-cache";

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: { } body } result)
        {
            var request = context.HttpContext.Request;
            var response = context.HttpContext.Response;
            var isGet = HttpMethods.IsGet(request.Method);

            if (body is IVersionedResource { Version: { } version })
            {
                // ETag = token di versione (reversibile in If-Match). Vale su GET 200 e sulle scritture
                // (PUT 200 / POST 201): qualunque risposta che trasporta una risorsa versionata.
                var etag = ETag.FromVersion(version);
                response.Headers.ETag = etag;

                if (isGet)
                {
                    response.Headers.CacheControl = CacheControlValue;
                    if (IfNoneMatchSatisfied(request, etag))
                        context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
                }
            }
            else if (isGet && IsOkStatus(result.StatusCode))
            {
                // Risorse non versionate (liste): ETag per-rappresentazione, solo caching.
                var payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerOptions.Web);
                var etag = ETag.Compute(payload);
                response.Headers.ETag = etag;
                response.Headers.CacheControl = CacheControlValue;

                if (IfNoneMatchSatisfied(request, etag))
                    context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
            }
        }

        await next();
    }

    // Ok(dto) ⇒ OkObjectResult con StatusCode 200; alcuni ObjectResult lasciano StatusCode null (= 200).
    private static bool IsOkStatus(int? statusCode) =>
        statusCode is null or StatusCodes.Status200OK;

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
