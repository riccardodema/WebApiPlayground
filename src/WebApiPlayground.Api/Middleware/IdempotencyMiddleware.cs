using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebApiPlayground.Api.ErrorHandling;
using WebApiPlayground.Application.Idempotency;

namespace WebApiPlayground.Api.Middleware;

/// <summary>
/// Idempotency per le richieste non idempotenti (POST): se il client allega un header
/// <c>Idempotency-Key</c>, la prima richiesta viene eseguita e la sua risposta memorizzata; ogni
/// ritentativo con la stessa chiave <b>rigioca</b> quella risposta senza ri-eseguire la scrittura
/// (semantica exactly-once → niente duplicati su retry). Vedi <c>.claude/context/idempotency.md</c>.
///
/// <para>Scelte: middleware (non filtro) per catturare la risposta HTTP reale — header
/// <c>Location</c> generato + body serializzato — bufferizzando lo stream. La chiave di storage è
/// scopata per client (claim utente) + metodo + path, così client diversi non collidono. Si salva
/// anche un fingerprint del body: stessa chiave + payload diverso → 422. Si memorizzano risposte
/// 2xx–4xx (esiti deterministici), mai 5xx (un errore transitorio dev'essere ritentabile).</para>
/// </summary>
public sealed class IdempotencyMiddleware
{
    public const string ReplayedHeader = "Idempotency-Replayed";

    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly KeyedAsyncLock _lock;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        KeyedAsyncLock keyedLock,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keyedLock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _store = store;
        _lock = keyedLock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Self-guard: l'idempotency si applica solo ai POST con la chiave presente (opt-in del client).
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !context.Request.Headers.TryGetValue(_options.HeaderName, out var keyValues))
        {
            await _next(context);
            return;
        }

        var idempotencyKey = keyValues.ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > _options.MaxKeyLength)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                "Invalid Idempotency-Key.",
                $"The '{_options.HeaderName}' header must be a non-empty string up to {_options.MaxKeyLength} characters.",
                "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1");
            return;
        }

        var fingerprint = await ComputeRequestFingerprintAsync(context.Request);
        var storageKey = BuildStorageKey(context, idempotencyKey);

        // Lock per-chiave: due richieste con la stessa chiave vengono serializzate nello stesso
        // processo, così la seconda trova il record e rigioca invece di ri-eseguire.
        using var _ = await _lock.LockAsync(storageKey, context.RequestAborted);

        var existing = await _store.GetAsync(storageKey, context.RequestAborted);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Idempotency-Key reused with a different payload (key hash {StorageKey}) → 422", storageKey);
                await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity,
                    "Idempotency-Key reuse with a different request.",
                    $"This '{_options.HeaderName}' was already used for a request with a different payload.",
                    "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.21");
                return;
            }

            _logger.LogInformation("Replaying stored response for Idempotency-Key (storage key {StorageKey})", storageKey);
            await ReplayAsync(context, existing);
            return;
        }

        await ExecuteAndStoreAsync(context, storageKey, fingerprint);
    }

    /// <summary>Esegue la pipeline catturando la risposta reale, la inoltra al client e la memorizza (se 2xx–4xx).</summary>
    private async Task ExecuteAndStoreAsync(HttpContext context, string storageKey, string fingerprint)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        // Inoltra al client il body bufferizzato.
        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted);

        var statusCode = context.Response.StatusCode;
        if (statusCode is < 200 or >= 500)
        {
            // 5xx (o status non deterministici): NON memorizzare, così un retry può davvero riprovare.
            _logger.LogDebug("Response {StatusCode} not stored for idempotency (only 2xx–4xx are)", statusCode);
            return;
        }

        var record = new IdempotencyRecord(
            statusCode,
            context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
            context.Response.ContentType ?? "application/json",
            Encoding.UTF8.GetString(buffer.ToArray()),
            fingerprint);

        await _store.SaveAsync(storageKey, record, _options.Ttl, context.RequestAborted);
        _logger.LogInformation("Stored response {StatusCode} for idempotency (storage key {StorageKey})", statusCode, storageKey);
    }

    private static async Task ReplayAsync(HttpContext context, IdempotencyRecord record)
    {
        context.Response.StatusCode = record.StatusCode;
        context.Response.ContentType = record.ContentType;
        if (record.Location is not null)
            context.Response.Headers.Location = record.Location;
        context.Response.Headers[ReplayedHeader] = "true";

        await context.Response.WriteAsync(record.Body, context.RequestAborted);
    }

    /// <summary>Fingerprint (SHA-256) del corpo della richiesta; il body viene bufferizzato e riavvolto per il binding.</summary>
    private static async Task<string> ComputeRequestFingerprintAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        var hash = await SHA256.HashDataAsync(request.Body, request.HttpContext.RequestAborted);
        request.Body.Position = 0;

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>storage key = SHA-256(userId | metodo | path | Idempotency-Key): scopata per client + endpoint.</summary>
    private static string BuildStorageKey(HttpContext context, string idempotencyKey)
    {
        var userId =
            context.User.FindFirstValue("oid") ??
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            "anonymous";

        var material = $"{userId}|{context.Request.Method}|{context.Request.Path}|{idempotencyKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"idem:{Convert.ToHexStringLower(hash)}";
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string title, string detail, string type)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = type,
            Instance = context.Request.Path,
        };
        ProblemDetailsEnricher.Enrich(context, problem);

        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            problem, problem.GetType(), options: null, contentType: "application/problem+json");
    }
}
