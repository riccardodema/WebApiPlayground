using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using WebApiPlayground.Api.OpenApi;
using Xunit;

namespace WebApiPlayground.Tests.OpenApi;

/// <summary>
/// I transformer che rendono il contratto OpenAPI "parlante" (caching condizionale, optimistic
/// concurrency, rate limiting, resilienza, security scheme): il contratto È comportamento osservabile
/// di questa API (vedi memoria di progetto: header/status documentati, mai impliciti). Si verifica
/// COSA finisce nel documento e su QUALI operazioni — e che le altre restino intoccate.
/// </summary>
public class ContractOperationTransformersTests
{
    private static OpenApiOperationTransformerContext Context(string method) => new()
    {
        DocumentName = "v1",
        Description = new ApiDescription { HttpMethod = method },
        ApplicationServices = new ServiceCollection().BuildServiceProvider(),
    };

    private static OpenApiOperation OperationWith200() => new()
    {
        Responses = new OpenApiResponses { ["200"] = new OpenApiResponse { Description = "OK" } },
    };

    // ---- CachingOperationTransformer -------------------------------------------

    [Fact]
    public async Task Caching_documents_conditional_get_on_GET_operations()
    {
        var operation = OperationWith200();

        await new CachingOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        var param = Assert.Single(operation.Parameters!);
        Assert.Equal("If-None-Match", param.Name);
        Assert.Equal(ParameterLocation.Header, param.In);
        Assert.False(param.Required); // il conditional GET è opt-in del client

        var ok = Assert.IsType<OpenApiResponse>(operation.Responses!["200"]);
        Assert.True(ok.Headers!.ContainsKey("ETag"));
        Assert.True(ok.Headers.ContainsKey("Cache-Control"));
        Assert.True(operation.Responses.ContainsKey("304"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Caching_leaves_non_GET_operations_untouched(string method)
    {
        var operation = OperationWith200();

        await new CachingOperationTransformer().TransformAsync(operation, Context(method), CancellationToken.None);

        Assert.Null(operation.Parameters);
        Assert.False(operation.Responses!.ContainsKey("304"));
    }

    [Fact]
    public async Task Caching_on_GET_without_a_200_still_documents_304_without_crashing()
    {
        var operation = new OpenApiOperation(); // nessuna response dichiarata

        await new CachingOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        Assert.True(operation.Responses!.ContainsKey("304"));
    }

    // ---- ConcurrencyOperationTransformer ----------------------------------------

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Concurrency_documents_required_if_match_and_412_428_on_conditional_writes(string method)
    {
        var operation = OperationWith200();

        await new ConcurrencyOperationTransformer().TransformAsync(operation, Context(method), CancellationToken.None);

        var param = Assert.Single(operation.Parameters!);
        Assert.Equal("If-Match", param.Name);
        Assert.True(param.Required); // qui l'header è OBBLIGATORIO (428 se manca)
        Assert.True(operation.Responses!.ContainsKey("412"));
        Assert.True(operation.Responses.ContainsKey("428"));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Concurrency_leaves_reads_and_creates_untouched(string method)
    {
        var operation = OperationWith200();

        await new ConcurrencyOperationTransformer().TransformAsync(operation, Context(method), CancellationToken.None);

        Assert.Null(operation.Parameters);
        Assert.False(operation.Responses!.ContainsKey("412"));
        Assert.False(operation.Responses.ContainsKey("428"));
    }

    // ---- RateLimitingOperationTransformer ----------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Rate_limiting_documents_429_with_retry_after_on_every_operation(string method)
    {
        var operation = OperationWith200();

        await new RateLimitingOperationTransformer().TransformAsync(operation, Context(method), CancellationToken.None);

        var tooMany = Assert.IsType<OpenApiResponse>(operation.Responses!["429"]);
        Assert.True(tooMany.Headers!.ContainsKey("Retry-After"));
        Assert.False(string.IsNullOrWhiteSpace(tooMany.Description));
    }

    // ---- ResilienceOperationTransformer ------------------------------------------

    [Fact]
    public async Task Resilience_enriches_only_operations_that_already_declare_503()
    {
        var operation = new OpenApiOperation
        {
            Responses = new OpenApiResponses { ["503"] = new OpenApiResponse { Description = "stub" } },
        };

        await new ResilienceOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        var unavailable = Assert.IsType<OpenApiResponse>(operation.Responses!["503"]);
        Assert.True(unavailable.Headers!.ContainsKey("Retry-After"));
        Assert.Contains("resilienza", unavailable.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resilience_leaves_operations_without_503_untouched()
    {
        var operation = OperationWith200();

        await new ResilienceOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        Assert.False(operation.Responses!.ContainsKey("503"));
        var ok = Assert.IsType<OpenApiResponse>(operation.Responses["200"]);
        Assert.Null(ok.Headers); // nessun effetto collaterale sulle altre response
    }

    [Fact]
    public async Task Resilience_tolerates_operations_with_no_responses_at_all()
    {
        var operation = new OpenApiOperation();

        await new ResilienceOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        Assert.True(operation.Responses is null || !operation.Responses.ContainsKey("503"));
    }

    // ---- Le DESCRIZIONI del contratto: i fatti portanti devono esserci -------------
    // (un contratto muto è un contratto rotto: il client decide dai testi cosa fare con 304/412/429/503)

    [Fact]
    public async Task Caching_descriptions_carry_the_conditional_get_facts()
    {
        var operation = OperationWith200();
        await new CachingOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        var ifNoneMatch = Assert.Single(operation.Parameters!);
        Assert.Contains("304", ifNoneMatch.Description);
        Assert.Equal(JsonSchemaType.String, Assert.IsType<OpenApiSchema>(ifNoneMatch.Schema).Type);

        var ok = Assert.IsType<OpenApiResponse>(operation.Responses!["200"]);
        Assert.Contains("If-None-Match", ok.Headers!["ETag"].Description);
        Assert.Contains("private, no-cache", ok.Headers["Cache-Control"].Description);
        Assert.Contains("If-None-Match", operation.Responses["304"].Description);
    }

    [Fact]
    public async Task Concurrency_descriptions_explain_the_lost_update_protocol()
    {
        var operation = OperationWith200();
        await new ConcurrencyOperationTransformer().TransformAsync(operation, Context("PUT"), CancellationToken.None);

        var ifMatch = Assert.Single(operation.Parameters!);
        Assert.Contains("lost update", ifMatch.Description);
        Assert.Contains("428", ifMatch.Description); // l'header dichiara le conseguenze di entrambe le violazioni
        Assert.Contains("412", ifMatch.Description);
        Assert.Equal(JsonSchemaType.String, Assert.IsType<OpenApiSchema>(ifMatch.Schema).Type);

        Assert.Contains("stale", operation.Responses!["412"].Description);
        Assert.Contains("If-Match mancante", operation.Responses["428"].Description);
    }

    [Fact]
    public async Task Rate_limiting_retry_after_is_documented_as_integer_seconds()
    {
        var operation = OperationWith200();
        await new RateLimitingOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        var tooMany = Assert.IsType<OpenApiResponse>(operation.Responses!["429"]);
        Assert.Contains("ProblemDetails", tooMany.Description);
        Assert.Contains("Retry-After", tooMany.Description);
        var retryAfter = tooMany.Headers!["Retry-After"];
        Assert.Contains("Secondi", retryAfter.Description);
        Assert.Equal(JsonSchemaType.Integer, Assert.IsType<OpenApiSchema>(retryAfter.Schema).Type);
    }

    [Fact]
    public async Task Resilience_retry_after_is_documented_as_integer_seconds()
    {
        var operation = new OpenApiOperation
        {
            Responses = new OpenApiResponses { ["503"] = new OpenApiResponse { Description = "stub" } },
        };
        await new ResilienceOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        var unavailable = Assert.IsType<OpenApiResponse>(operation.Responses!["503"]);
        Assert.Contains("Retry-After", unavailable.Description);
        var retryAfter = unavailable.Headers!["Retry-After"];
        Assert.Contains("Secondi", retryAfter.Description);
        Assert.Equal(JsonSchemaType.Integer, Assert.IsType<OpenApiSchema>(retryAfter.Schema).Type);
    }

    // ---- Idempotenza dei transformer: ciò che c'è già non si butta -------------------

    [Fact]
    public async Task Transformers_append_to_existing_parameters_instead_of_replacing_them()
    {
        var operation = OperationWith200();
        operation.Parameters = [new OpenApiParameter { Name = "pageNumber", In = ParameterLocation.Query }];

        await new CachingOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);
        await new ConcurrencyOperationTransformer().TransformAsync(operation, Context("PUT"), CancellationToken.None);

        // ??= non deve diventare un assegnamento: i parametri preesistenti sopravvivono.
        Assert.Equal(["pageNumber", "If-None-Match", "If-Match"], operation.Parameters.Select(p => p.Name));
    }

    [Fact]
    public async Task Rate_limiting_preserves_already_declared_responses()
    {
        var operation = OperationWith200();

        await new RateLimitingOperationTransformer().TransformAsync(operation, Context("GET"), CancellationToken.None);

        Assert.True(operation.Responses!.ContainsKey("200")); // il 429 si AGGIUNGE, non sostituisce
        Assert.True(operation.Responses.ContainsKey("429"));
    }

    // ---- BearerSecuritySchemeTransformer -----------------------------------------

    [Fact]
    public async Task Bearer_registers_the_jwt_scheme_and_a_global_requirement()
    {
        var document = new OpenApiDocument();
        var context = new OpenApiDocumentTransformerContext
        {
            DocumentName = "v1",
            ApplicationServices = new ServiceCollection().BuildServiceProvider(),
            DescriptionGroups = [],
        };

        await new BearerSecuritySchemeTransformer().TransformAsync(document, context, CancellationToken.None);

        var scheme = Assert.IsType<OpenApiSecurityScheme>(document.Components!.SecuritySchemes!["Bearer"]);
        Assert.Equal(SecuritySchemeType.Http, scheme.Type);
        Assert.Equal("bearer", scheme.Scheme);
        Assert.Equal("JWT", scheme.BearerFormat);
        Assert.Single(document.Security!); // il requirement vale per TUTTO il documento
    }

    [Fact]
    public async Task Bearer_preserves_existing_components_and_security_requirements()
    {
        // Componenti/requirement preesistenti: i ??= non devono diventare assegnamenti che li azzerano.
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["ApiKey"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey },
                },
            },
            Security = [new OpenApiSecurityRequirement()],
        };
        var context = new OpenApiDocumentTransformerContext
        {
            DocumentName = "v1",
            ApplicationServices = new ServiceCollection().BuildServiceProvider(),
            DescriptionGroups = [],
        };

        await new BearerSecuritySchemeTransformer().TransformAsync(document, context, CancellationToken.None);

        Assert.True(document.Components!.SecuritySchemes!.ContainsKey("ApiKey")); // schema preesistente sopravvive
        Assert.True(document.Components.SecuritySchemes.ContainsKey("Bearer"));   // + il Bearer aggiunto
        Assert.Equal(2, document.Security!.Count);                               // il requirement si AGGIUNGE
    }
}
