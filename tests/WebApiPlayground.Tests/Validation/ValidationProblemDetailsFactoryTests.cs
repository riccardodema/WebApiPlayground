using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using WebApiPlayground.Api.Middleware;
using WebApiPlayground.Api.Validation;
using Xunit;

namespace WebApiPlayground.Tests.Validation;

/// <summary>
/// La risposta 400 di validazione come la vede il CLIENT: ValidationProblemDetails con la mappa
/// <c>errors</c> campo→messaggi, content type <c>application/problem+json</c>, instance = path,
/// e il correlationId per incrociare i log — identica per FluentValidation e model binding.
/// </summary>
public class ValidationProblemDetailsFactoryTests
{
    private static ActionContext BuildActionContext(string path = "/api/v1/books")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();
        httpContext.Items[CorrelationIdMiddleware.ItemKey] = "corr-400";
        return new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
    }

    private static async Task<JsonDocument> ExecuteAsync(ActionContext context)
    {
        var result = ValidationProblemDetailsFactory.Create(context);
        await result.ExecuteResultAsync(context);
        context.HttpContext.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.HttpContext.Response.Body);
    }

    [Fact]
    public async Task Response_is_a_400_problem_json_with_the_errors_map()
    {
        var context = BuildActionContext();
        context.ModelState.AddModelError("Title", "Title must not be empty.");
        context.ModelState.AddModelError("Title", "Title must be at most 100 characters.");
        context.ModelState.AddModelError("AuthorId", "AuthorId must be greater than 0.");

        var json = await ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.HttpContext.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.HttpContext.Response.ContentType);

        var root = json.RootElement;
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal("One or more validation errors occurred.", root.GetProperty("title").GetString());
        Assert.Contains("errors", root.GetProperty("detail").GetString()); // il detail INDIRIZZA alla mappa

        var errors = root.GetProperty("errors");
        Assert.Equal(2, errors.GetProperty("Title").GetArrayLength()); // TUTTI i messaggi, non solo il primo
        Assert.Equal(1, errors.GetProperty("AuthorId").GetArrayLength());
    }

    [Fact]
    public async Task Instance_is_the_request_path_and_correlation_id_is_attached()
    {
        var context = BuildActionContext(path: "/api/v1/books/7");
        context.ModelState.AddModelError("Title", "required");

        var json = await ExecuteAsync(context);

        Assert.Equal("/api/v1/books/7", json.RootElement.GetProperty("instance").GetString());
        Assert.Equal("corr-400", json.RootElement.GetProperty("correlationId").GetString());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _)); // correlazione col tracing
    }
}
