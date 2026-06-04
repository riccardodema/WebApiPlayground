using Scalar.AspNetCore;
using Serilog;
using WebApiPlayground.Api.Extensions;
using WebApiPlayground.Api.HealthChecks;
using WebApiPlayground.Api.Http;
using WebApiPlayground.Api.Middleware;
using WebApiPlayground.Api.OpenApi;
using WebApiPlayground.Api.Validation;
using WebApiPlayground.Application;
using WebApiPlayground.Infrastructure;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting WebApiPlayground API");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    // Il ValidationFilter (FluentValidation) gira su ogni action; le violazioni e quelle
    // di model binding (DataAnnotations) producono la STESSA risposta 400 ProblemDetails
    // tramite InvalidModelStateResponseFactory.
    // ETagResultFilter: HTTP caching (ETag/Cache-Control/304) sui GET.
    builder.Services.AddControllers(options =>
        {
            options.Filters.Add<ValidationFilter>();
            options.Filters.Add<ETagResultFilter>();
        })
        .ConfigureApiBehaviorOptions(options =>
            options.InvalidModelStateResponseFactory = ValidationProblemDetailsFactory.Create);

    builder.Services.AddApiProblemDetails();
    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        // Proietta le regole FluentValidation nello schema (required/maxLength/minimum + descrizione).
        options.AddSchemaTransformer<FluentValidationSchemaTransformer>();
        // Documenta nel contratto l'idempotency dei POST (header Idempotency-Key/Replayed + 422).
        options.AddOperationTransformer<IdempotencyOperationTransformer>();
        // Documenta nel contratto l'HTTP caching dei GET (ETag/Cache-Control/If-None-Match + 304).
        options.AddOperationTransformer<CachingOperationTransformer>();
    });

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddApiAuthentication(builder.Configuration, builder.Environment);
    builder.Services.AddApiAuthorization();

    var app = builder.Build();

    app.UseMiddleware<CorrelationIdMiddleware>();

    // Subito dopo il correlation id (così il body d'errore lo include) e prima del resto
    // della pipeline: ogni eccezione non gestita a valle diventa un ProblemDetails 500.
    app.UseExceptionHandler();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000} ms";
    });

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    if (!app.Environment.IsDevelopment())
        app.UseHttpsRedirection();

    app.UseAuthentication();
    app.UseAuthorization();

    // Dopo l'autorizzazione: la storage key dell'idempotency è scopata per client (claim utente).
    app.UseMiddleware<IdempotencyMiddleware>();

    app.MapApiHealthChecks();
    app.MapControllers();

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException && ex.GetType().Name != "StopTheHostException")
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
