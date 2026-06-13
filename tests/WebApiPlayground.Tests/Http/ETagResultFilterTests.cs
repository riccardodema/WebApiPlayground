using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using WebApiPlayground.Api.Http;
using WebApiPlayground.Application.Concurrency;
using Xunit;

namespace WebApiPlayground.Tests.Http;

/// <summary>
/// Comportamento del filtro ETag osservato sul risultato: risorse VERSIONATE espongono il token di
/// versione come ETag (su GET e scritture), le liste non versionate un'impronta del payload (solo
/// GET 200), e un <c>If-None-Match</c> combaciante corto-circuita in 304. I casi fuori scope
/// (non-ObjectResult, status non 200) restano intoccati.
/// </summary>
public class ETagResultFilterTests
{
    private sealed record VersionedDto(int Id, string Title) : IVersionedResource
    {
        public string? Version { get; init; }
    }

    private static ResultExecutingContext BuildContext(IActionResult result, string method = "GET", string? ifNoneMatch = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        if (ifNoneMatch is not null)
            httpContext.Request.Headers.IfNoneMatch = ifNoneMatch;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(actionContext, [], result, controller: new object());
    }

    private static async Task<ResultExecutingContext> RunAsync(ResultExecutingContext context)
    {
        await new ETagResultFilter().OnResultExecutionAsync(
            context, () => Task.FromResult<ResultExecutedContext>(null!));
        return context;
    }

    private const string VersionToken = "AAAAAAAAAAE="; // base64 di un rowversion fittizio

    // ---- Risorse versionate -----------------------------------------------------

    [Fact]
    public async Task Versioned_resource_on_GET_exposes_version_etag_and_cache_control()
    {
        var context = BuildContext(new OkObjectResult(new VersionedDto(1, "Dune") { Version = VersionToken }));

        await RunAsync(context);

        Assert.Equal($"\"{VersionToken}\"", context.HttpContext.Response.Headers.ETag);
        Assert.Equal("private, no-cache", context.HttpContext.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Versioned_resource_on_write_exposes_etag_but_no_caching()
    {
        // Su POST/PUT l'ETag serve per il PROSSIMO If-Match (concorrenza), non per il caching.
        var context = BuildContext(
            new OkObjectResult(new VersionedDto(1, "Dune") { Version = VersionToken }), method: "PUT");

        await RunAsync(context);

        Assert.Equal($"\"{VersionToken}\"", context.HttpContext.Response.Headers.ETag);
        Assert.True(string.IsNullOrEmpty(context.HttpContext.Response.Headers.CacheControl));
    }

    [Fact]
    public async Task Matching_if_none_match_on_versioned_GET_short_circuits_to_304()
    {
        var context = BuildContext(
            new OkObjectResult(new VersionedDto(1, "Dune") { Version = VersionToken }),
            ifNoneMatch: $"\"{VersionToken}\"");

        await RunAsync(context);

        var status = Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public async Task Matching_if_none_match_on_a_write_does_NOT_produce_304()
    {
        // 304 è semantica di lettura condizionale: una PUT con If-None-Match combaciante va eseguita normalmente.
        var context = BuildContext(
            new OkObjectResult(new VersionedDto(1, "Dune") { Version = VersionToken }),
            method: "PUT", ifNoneMatch: $"\"{VersionToken}\"");

        await RunAsync(context);

        Assert.IsType<OkObjectResult>(context.Result);
    }

    // ---- Risorse non versionate (liste) ------------------------------------------

    [Fact]
    public async Task Unversioned_body_on_GET_gets_a_representation_etag()
    {
        var context = BuildContext(new OkObjectResult(new { items = new[] { "a", "b" } }));

        await RunAsync(context);

        var etag = context.HttpContext.Response.Headers.ETag.ToString();
        Assert.StartsWith("\"", etag);
        Assert.Equal(66, etag.Length); // sha-256 hex (64) + 2 virgolette: impronta forte
        Assert.Equal("private, no-cache", context.HttpContext.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Same_payload_produces_the_same_etag_and_matching_client_gets_304()
    {
        var first = await RunAsync(BuildContext(new OkObjectResult(new { v = 1 })));
        var etag = first.HttpContext.Response.Headers.ETag.ToString();

        var second = await RunAsync(BuildContext(new OkObjectResult(new { v = 1 }), ifNoneMatch: etag));

        var status = Assert.IsType<StatusCodeResult>(second.Result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public async Task If_none_match_star_matches_any_representation()
    {
        var context = BuildContext(new OkObjectResult(new { v = 1 }), ifNoneMatch: "*");

        await RunAsync(context);

        var status = Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public async Task Stale_if_none_match_does_not_short_circuit()
    {
        var context = BuildContext(new OkObjectResult(new { v = 1 }), ifNoneMatch: "\"deadbeef\"");

        await RunAsync(context);

        Assert.IsType<OkObjectResult>(context.Result); // rappresentazione cambiata → risposta piena
    }

    // ---- Fuori scope ---------------------------------------------------------------

    [Fact]
    public async Task Non_object_results_are_left_untouched()
    {
        var context = BuildContext(new StatusCodeResult(StatusCodes.Status204NoContent));

        await RunAsync(context);

        Assert.True(string.IsNullOrEmpty(context.HttpContext.Response.Headers.ETag));
    }

    [Fact]
    public async Task Unversioned_non_200_object_results_get_no_etag()
    {
        var context = BuildContext(new ObjectResult(new { error = "x" }) { StatusCode = 400 });

        await RunAsync(context);

        Assert.True(string.IsNullOrEmpty(context.HttpContext.Response.Headers.ETag));
    }
}
