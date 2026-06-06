using Scalar.AspNetCore;
using Serilog;
using Serilog.Sinks.OpenTelemetry;
using WebApiPlayground.Api.Configuration;
using WebApiPlayground.Api.Extensions;
using WebApiPlayground.Api.HealthChecks;
using WebApiPlayground.Api.Http;
using WebApiPlayground.Api.Middleware;
using WebApiPlayground.Api.Observability;
using WebApiPlayground.Api.Validation;
using WebApiPlayground.Api.Versioning;
using WebApiPlayground.Application;
using WebApiPlayground.Infrastructure;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting WebApiPlayground API");

    var builder = WebApplication.CreateBuilder(args);

    // Fail-fast: fuori da Development (es. immagine in Production) rifiuta l'avvio se manca
    // configurazione obbligatoria, elencando esattamente cosa impostare. In Development è un no-op.
    StartupConfigurationValidator.ValidateRequiredConfiguration(builder.Configuration, builder.Environment);

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services);

        // Bridge Serilog → OTLP (config-gated come l'export di traces/metrics). Quando OtlpEndpoint è
        // valorizzato, i log Serilog escono anche come record OTLP: il sink riattacca TraceId/SpanId
        // dall'Activity corrente (log ↔ trace) e porta con sé le property del LogContext, incluso il
        // CorrelationId. Il sink Console resta invariato. Vedi .claude/context/opentelemetry.md.
        var otlpEndpoint = context.Configuration[$"{OpenTelemetryOptions.SectionName}:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            var serviceName = context.Configuration[$"{OpenTelemetryOptions.SectionName}:ServiceName"]
                              ?? "WebApiPlayground.Api";

            configuration.WriteTo.OpenTelemetry(sink =>
            {
                sink.Endpoint = otlpEndpoint;
                sink.Protocol = OtlpProtocol.Grpc;
                sink.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = serviceName };
                sink.IncludedData = IncludedData.TraceIdField | IncludedData.SpanIdField
                    | IncludedData.MessageTemplateTextAttribute | IncludedData.SpecRequiredResourceAttributes;
            });
        }
    });

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
    // OpenTelemetry: traces + metrics (auto-instrumentation + source/meter custom), export config-gated.
    // I log seguono il bridge Serilog → OTLP configurato sopra in UseSerilog.
    builder.Services.AddApiObservability(builder.Configuration, builder.Environment);
    builder.Services.AddApiRateLimiting(builder.Configuration);
    // API versioning (segmento URL) + un documento OpenAPI per versione, con i transformer condivisi
    // (auth, validazione, idempotency, caching, rate limiting, versioning). Vedi ApiVersioningExtensions.
    builder.Services.AddApiVersioningWithOpenApi();

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
        // Serve /openapi/v1.json e /openapi/v2.json (un documento per versione registrato in DI).
        app.MapOpenApi();
        // Scalar con un documento per versione: l'ultima (ApiVersions.All) è quella di default.
        app.MapScalarApiReference(options =>
        {
            foreach (var version in ApiVersions.All)
            {
                var group = ApiVersions.GroupName(version);
                options.AddDocument(group, group);
            }
        });
    }

    if (!app.Environment.IsDevelopment())
        app.UseHttpsRedirection();

    app.UseAuthentication();
    app.UseAuthorization();

    // Dopo l'autorizzazione (così la partizione vede il claim utente) e prima dell'idempotency:
    // rifiuta presto le richieste in eccesso, prima del buffering del body fatto dal middleware
    // di idempotency. Vedi .claude/lessons.md [L15].
    app.UseRateLimiter();

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
