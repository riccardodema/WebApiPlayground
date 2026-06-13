using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebApiPlayground.Api.Middleware;
using WebApiPlayground.Application.Idempotency;
using Xunit;

namespace WebApiPlayground.Tests.Idempotency;

/// <summary>
/// Comportamento del middleware di idempotency, osservato SOLO dall'esterno (richiesta → risposta +
/// effetti sullo store): passthrough per metodi/richieste fuori scope, validazione della chiave ai
/// bordi (vuota, troppo lunga, esattamente al limite), prima esecuzione memorizzata, replay con
/// header dedicato, 422 su riuso con payload diverso, 5xx mai memorizzati, scoping per client.
/// Store fake in-memory: niente mock del comportamento interno.
/// </summary>
public class IdempotencyMiddlewareTests
{
    private sealed class FakeStore : IIdempotencyStore
    {
        public readonly Dictionary<string, (IdempotencyRecord Record, TimeSpan Ttl)> Saved = new(StringComparer.Ordinal);

        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved.TryGetValue(key, out var entry) ? entry.Record : null);

        public Task SaveAsync(string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            Saved[key] = (record, ttl);
            return Task.CompletedTask;
        }
    }

    private static readonly IdempotencyOptions Options = new();

    private static IdempotencyMiddleware BuildMiddleware(RequestDelegate next, FakeStore store) =>
        new(next, store, new KeyedAsyncLock(), Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<IdempotencyMiddleware>.Instance);

    private static DefaultHttpContext BuildContext(
        string method = "POST", string? key = "key-1", string body = """{"title":"Dune"}""", string path = "/api/v1/books")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (key is not null)
            context.Request.Headers[Options.HeaderName] = key;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    /// <summary>Next che risponde 201 con Location e un body, contando le invocazioni.</summary>
    private sealed class CountingNext
    {
        public int Invocations { get; private set; }
        public int StatusCode { get; set; } = StatusCodes.Status201Created;

        public async Task InvokeAsync(HttpContext context)
        {
            Invocations++;
            context.Response.StatusCode = StatusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers.Location = "/api/v1/books/42";
            await context.Response.WriteAsync("""{"id":42}""");
        }
    }

    // ---- Fuori scope: passthrough senza toccare lo store ----------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Non_post_methods_pass_through_untouched(string method)
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        await middleware.InvokeAsync(BuildContext(method: method));

        Assert.Equal(1, next.Invocations);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Post_without_the_header_passes_through_untouched()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        await middleware.InvokeAsync(BuildContext(key: null));

        Assert.Equal(1, next.Invocations);
        Assert.Empty(store.Saved);
    }

    // ---- Validazione della chiave ai bordi -------------------------------------

    [Fact]
    public async Task Blank_key_is_rejected_with_400_problem_details_without_executing()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);
        var context = BuildContext(key: "   ");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(0, next.Invocations); // la scrittura NON va eseguita
        Assert.Contains("Idempotency-Key", ResponseBody(context));
    }

    [Fact]
    public async Task Key_longer_than_the_limit_is_rejected_with_400()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);
        var context = BuildContext(key: new string('k', Options.MaxKeyLength + 1));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(0, next.Invocations);
    }

    [Fact]
    public async Task Key_exactly_at_the_limit_is_accepted()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);
        var context = BuildContext(key: new string('k', Options.MaxKeyLength)); // boundary: == limite

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
        Assert.Equal(1, next.Invocations);
        Assert.Single(store.Saved);
    }

    // ---- Prima esecuzione: eseguita, inoltrata e memorizzata -------------------

    [Fact]
    public async Task First_request_executes_and_stores_the_full_response()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);
        var context = BuildContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
        Assert.Equal("""{"id":42}""", ResponseBody(context)); // body inoltrato al client
        var record = Assert.Single(store.Saved).Value;
        Assert.Equal(StatusCodes.Status201Created, record.Record.StatusCode);
        Assert.Equal("/api/v1/books/42", record.Record.Location);
        Assert.Equal("""{"id":42}""", record.Record.Body);
        Assert.Equal(Options.Ttl, record.Ttl); // TTL dalle opzioni, non hardcoded
    }

    // ---- Replay: stessa chiave + stesso payload --------------------------------

    [Fact]
    public async Task Retry_with_same_key_and_payload_replays_without_reexecuting()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        await middleware.InvokeAsync(BuildContext());
        var retry = BuildContext();
        await middleware.InvokeAsync(retry);

        Assert.Equal(1, next.Invocations); // la scrittura è avvenuta UNA volta sola
        Assert.Equal(StatusCodes.Status201Created, retry.Response.StatusCode);
        Assert.Equal("""{"id":42}""", ResponseBody(retry));
        Assert.Equal("/api/v1/books/42", retry.Response.Headers.Location);
        Assert.Equal("true", retry.Response.Headers[IdempotencyMiddleware.ReplayedHeader]);
    }

    // ---- Riuso con payload diverso → 422 ---------------------------------------

    [Fact]
    public async Task Same_key_with_different_payload_is_rejected_with_422()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        await middleware.InvokeAsync(BuildContext(body: """{"title":"Dune"}"""));
        var conflicting = BuildContext(body: """{"title":"Fondazione"}""");
        await middleware.InvokeAsync(conflicting);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, conflicting.Response.StatusCode);
        Assert.Equal(1, next.Invocations); // mai ri-eseguita
        Assert.Contains("different", ResponseBody(conflicting));
    }

    // ---- 5xx: mai memorizzati (il retry deve poter riprovare) -------------------

    [Fact]
    public async Task Server_errors_are_not_stored_so_a_retry_really_retries()
    {
        var store = new FakeStore();
        var next = new CountingNext { StatusCode = StatusCodes.Status500InternalServerError };
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        await middleware.InvokeAsync(BuildContext());
        Assert.Empty(store.Saved);

        next.StatusCode = StatusCodes.Status201Created;
        await middleware.InvokeAsync(BuildContext());

        Assert.Equal(2, next.Invocations); // il retry ha davvero ri-eseguito
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task Client_errors_4xx_are_stored_as_deterministic_outcomes()
    {
        var store = new FakeStore();
        var next = new CountingNext { StatusCode = StatusCodes.Status409Conflict };
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        await middleware.InvokeAsync(BuildContext());
        await middleware.InvokeAsync(BuildContext());

        Assert.Equal(1, next.Invocations); // anche un 409 si rigioca: esito deterministico
        Assert.Single(store.Saved);
    }

    // ---- Scoping: client/percorsi diversi non collidono -------------------------

    [Fact]
    public async Task Same_key_on_different_paths_does_not_collide()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        await middleware.InvokeAsync(BuildContext(path: "/api/v1/books"));
        await middleware.InvokeAsync(BuildContext(path: "/api/v1/authors"));

        Assert.Equal(2, next.Invocations); // chiavi di storage diverse → entrambe eseguite
        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public async Task Stored_content_type_falls_back_to_json_when_next_sets_none()
    {
        var store = new FakeStore();
        var middleware = BuildMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK; // boundary: 200 È memorizzabile
            await ctx.Response.WriteAsync("plain");            // nessun ContentType esplicito
        }, store);

        await middleware.InvokeAsync(BuildContext());
        var replay = BuildContext();
        await middleware.InvokeAsync(replay);

        var record = Assert.Single(store.Saved).Value.Record;
        Assert.Equal(StatusCodes.Status200OK, record.StatusCode);
        Assert.Equal("application/json", record.ContentType); // fallback dichiarato del replay
        Assert.Equal("application/json", replay.Response.ContentType);
        Assert.True(string.IsNullOrEmpty(record.Location));   // niente Location se next non l'ha messa
    }

    [Fact]
    public async Task Request_body_is_rewound_so_model_binding_downstream_can_read_it()
    {
        string? bodySeenByNext = null;
        var middleware = BuildMiddleware(async ctx =>
        {
            // Il fingerprint ha già letto lo stream: senza rewind il binding a valle leggerebbe vuoto.
            using var reader = new StreamReader(ctx.Request.Body);
            bodySeenByNext = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = StatusCodes.Status201Created;
        }, new FakeStore());

        await middleware.InvokeAsync(BuildContext(body: """{"title":"Dune"}"""));

        Assert.Equal("""{"title":"Dune"}""", bodySeenByNext);
    }

    [Fact]
    public async Task Authenticated_user_via_name_identifier_is_scoped_apart_from_anonymous()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        var anonymous = BuildContext();
        var withNameId = BuildContext();
        withNameId.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-7")], "test"));

        await middleware.InvokeAsync(anonymous);
        await middleware.InvokeAsync(withNameId);

        // La catena oid → NameIdentifier → anonymous deve distinguere i tre livelli.
        Assert.Equal(2, next.Invocations);
        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public async Task Rejections_carry_problem_json_with_the_correlation_id()
    {
        var context = BuildContext(key: "   ");
        context.Items[WebApiPlayground.Api.Middleware.CorrelationIdMiddleware.ItemKey] = "corr-idem";
        var middleware = BuildMiddleware(new CountingNext().InvokeAsync, new FakeStore());

        await middleware.InvokeAsync(context);

        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.Contains("corr-idem", ResponseBody(context)); // il 400 è incrociabile coi log
    }

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        var store = new FakeStore();
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        var logger = NullLogger<IdempotencyMiddleware>.Instance;
        RequestDelegate next = _ => Task.CompletedTask;

        Assert.Throws<ArgumentNullException>(() => new IdempotencyMiddleware(null!, store, new KeyedAsyncLock(), options, logger));
        Assert.Throws<ArgumentNullException>(() => new IdempotencyMiddleware(next, null!, new KeyedAsyncLock(), options, logger));
        Assert.Throws<ArgumentNullException>(() => new IdempotencyMiddleware(next, store, null!, options, logger));
        Assert.Throws<ArgumentNullException>(() => new IdempotencyMiddleware(next, store, new KeyedAsyncLock(), null!, logger));
        Assert.Throws<ArgumentNullException>(() => new IdempotencyMiddleware(next, store, new KeyedAsyncLock(), options, null!));
    }

    [Fact]
    public async Task Same_key_from_different_users_does_not_collide()
    {
        var store = new FakeStore();
        var next = new CountingNext();
        var middleware = BuildMiddleware(next.InvokeAsync, store);

        var alice = BuildContext();
        alice.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("oid", "alice")], "test"));
        var bob = BuildContext();
        bob.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("oid", "bob")], "test"));

        await middleware.InvokeAsync(alice);
        await middleware.InvokeAsync(bob);

        Assert.Equal(2, next.Invocations);
        Assert.Equal(2, store.Saved.Count);
    }
}
