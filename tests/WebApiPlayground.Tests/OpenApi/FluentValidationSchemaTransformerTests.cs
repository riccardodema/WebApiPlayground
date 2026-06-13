using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentValidation;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using WebApiPlayground.Api.OpenApi;
using Xunit;

namespace WebApiPlayground.Tests.OpenApi;

/// <summary>
/// Proiezione delle regole FluentValidation nello schema OpenAPI (<see cref="FluentValidationSchemaTransformer"/>):
/// i rami non esercitati dal contratto reale (min length, GreaterThanOrEqual, valore di confronto
/// NON numerico, proprietà senza regole) vengono coperti qui con un DTO e un validator di test.
/// I validator restano l'unica fonte di verità: lo schema li riflette, non li duplica.
/// </summary>
public class FluentValidationSchemaTransformerTests
{
    private sealed record Sample(string Title, int AuthorId, DateTime From, string Untouched);

    private sealed class SampleValidator : AbstractValidator<Sample>
    {
        public SampleValidator()
        {
            RuleFor(s => s.Title).MinimumLength(2);            // ILengthValidator con Min>0 (e Max assente)
            RuleFor(s => s.AuthorId).GreaterThanOrEqualTo(1);  // Comparison.GreaterThanOrEqual
            RuleFor(s => s.From).GreaterThan(DateTime.UnixEpoch); // confronto NON numerico → nessun Minimum
            // "Untouched" senza regole → la proprietà dello schema non deve essere toccata.
        }
    }

    private static async Task<OpenApiSchema> TransformAsync()
    {
        var schema = new OpenApiSchema
        {
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["authorId"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["from"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["untouched"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        };

        var services = new ServiceCollection()
            .AddSingleton<IValidator<Sample>>(new SampleValidator())
            .BuildServiceProvider();

        var context = new OpenApiSchemaTransformerContext
        {
            DocumentName = "v1",
            JsonTypeInfo = JsonTypeInfo.CreateJsonTypeInfo<Sample>(JsonSerializerOptions.Default),
            JsonPropertyInfo = null,
            ParameterDescription = null,
            ApplicationServices = services,
        };

        await new FluentValidationSchemaTransformer().TransformAsync(schema, context, CancellationToken.None);
        return schema;
    }

    [Fact]
    public async Task Minimum_length_is_projected_as_minLength()
    {
        var schema = await TransformAsync();

        var title = Assert.IsType<OpenApiSchema>(schema.Properties!["title"]);
        Assert.Equal(2, title.MinLength);
        Assert.Null(title.MaxLength); // MinimumLength non impone un massimo
    }

    [Fact]
    public async Task GreaterThanOrEqual_is_projected_as_inclusive_minimum()
    {
        var schema = await TransformAsync();

        var authorId = Assert.IsType<OpenApiSchema>(schema.Properties!["authorId"]);
        Assert.Equal("1", authorId.Minimum);
    }

    [Fact]
    public async Task Non_numeric_comparison_produces_no_numeric_bound()
    {
        var schema = await TransformAsync();

        // GreaterThan su una data: TryToLong fallisce in modo controllato → nessun vincolo numerico.
        var from = Assert.IsType<OpenApiSchema>(schema.Properties!["from"]);
        Assert.Null(from.Minimum);
    }

    [Fact]
    public async Task Properties_without_rules_are_left_untouched()
    {
        var schema = await TransformAsync();

        var untouched = Assert.IsType<OpenApiSchema>(schema.Properties!["untouched"]);
        Assert.Null(untouched.MinLength);
        Assert.Null(untouched.Maximum);
        Assert.Null(untouched.Description);
    }

    // ---- Rami completi delle regole (required, max/min length, GreaterThan numerico, descrizioni) ----

    private sealed record Full(string Title, string Code, int AuthorId, int Pages, string Untouched);

    private sealed class FullValidator : AbstractValidator<Full>
    {
        public FullValidator()
        {
            RuleFor(s => s.Title).NotEmpty().MaximumLength(100); // required + max
            RuleFor(s => s.Code).Length(2, 50);                  // min E max insieme
            RuleFor(s => s.AuthorId).NotEmpty();                 // required su NON-stringa: niente MinLength
            RuleFor(s => s.Pages).GreaterThan(0);                // GreaterThan NUMERICO → minimo esclusivo +1
        }
    }

    private static async Task<OpenApiSchema> TransformFullAsync(OpenApiSchema? prebuilt = null)
    {
        var schema = prebuilt ?? new OpenApiSchema
        {
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["authorId"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["pages"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["untouched"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        };

        var services = new ServiceCollection()
            .AddSingleton<IValidator<Full>>(new FullValidator())
            .BuildServiceProvider();

        var context = new OpenApiSchemaTransformerContext
        {
            DocumentName = "v1",
            JsonTypeInfo = JsonTypeInfo.CreateJsonTypeInfo<Full>(JsonSerializerOptions.Default),
            JsonPropertyInfo = null,
            ParameterDescription = null,
            ApplicationServices = services,
        };

        await new FluentValidationSchemaTransformer().TransformAsync(schema, context, CancellationToken.None);
        return schema;
    }

    [Fact]
    public async Task NotEmpty_marks_the_property_required_and_strings_get_minLength_one()
    {
        var schema = await TransformFullAsync();

        Assert.Contains("title", schema.Required!);
        var title = Assert.IsType<OpenApiSchema>(schema.Properties!["title"]);
        Assert.Equal(1, title.MinLength); // stringa required ⇒ almeno 1 carattere
    }

    [Fact]
    public async Task NotEmpty_on_a_non_string_adds_required_but_no_minLength()
    {
        var schema = await TransformFullAsync();

        Assert.Contains("authorId", schema.Required!);
        var authorId = Assert.IsType<OpenApiSchema>(schema.Properties!["authorId"]);
        Assert.Null(authorId.MinLength); // minLength è semantica da stringhe
    }

    [Fact]
    public async Task Maximum_and_minimum_lengths_are_both_projected()
    {
        var schema = await TransformFullAsync();

        var title = Assert.IsType<OpenApiSchema>(schema.Properties!["title"]);
        Assert.Equal(100, title.MaxLength);

        var code = Assert.IsType<OpenApiSchema>(schema.Properties!["code"]);
        Assert.Equal(2, code.MinLength);
        Assert.Equal(50, code.MaxLength);
    }

    [Fact]
    public async Task GreaterThan_numeric_projects_an_exclusive_bound_as_plus_one()
    {
        var schema = await TransformFullAsync();

        // GreaterThan(0) = minimo INCLUSIVO 1: il contratto deve dire "minimum 1", non "minimum 0".
        var pages = Assert.IsType<OpenApiSchema>(schema.Properties!["pages"]);
        Assert.Equal("1", pages.Minimum);
    }

    [Fact]
    public async Task Constraints_are_summarized_in_a_readable_description()
    {
        var schema = await TransformFullAsync();

        var title = Assert.IsType<OpenApiSchema>(schema.Properties!["title"]);
        Assert.Equal("Validation: required; max length 100.", title.Description);

        var code = Assert.IsType<OpenApiSchema>(schema.Properties!["code"]);
        Assert.Contains("max length 50", code.Description);
        Assert.Contains("min length 2", code.Description);

        var pages = Assert.IsType<OpenApiSchema>(schema.Properties!["pages"]);
        Assert.Equal("Validation: minimum 1.", pages.Description);
    }

    [Fact]
    public async Task Existing_descriptions_are_preserved_and_the_summary_is_appended()
    {
        var schema = new OpenApiSchema
        {
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Il titolo del libro." },
                ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["authorId"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["pages"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["untouched"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        };

        var transformed = await TransformFullAsync(schema);

        var title = Assert.IsType<OpenApiSchema>(transformed.Properties!["title"]);
        Assert.Equal("Il titolo del libro. Validation: required; max length 100.", title.Description);
    }

    [Fact]
    public async Task NotEmpty_does_not_shrink_an_already_stricter_minLength()
    {
        var schema = new OpenApiSchema
        {
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 5 },
                ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["authorId"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["pages"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["untouched"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        };

        var transformed = await TransformFullAsync(schema);

        var title = Assert.IsType<OpenApiSchema>(transformed.Properties!["title"]);
        Assert.Equal(5, title.MinLength); // un vincolo PIÙ stretto non va allentato a 1
    }

    // ---- Boundaries dei vincoli numerici/lunghezza ---------------------------------

    private sealed record ZeroBound(int Quantity);

    private sealed class ZeroBoundValidator : AbstractValidator<ZeroBound>
    {
        // GreaterThan(0): il minimo inclusivo è 1, NON 0 — il confine è il punto del bug.
        public ZeroBoundValidator() => RuleFor(x => x.Quantity).GreaterThan(0);
    }

    [Fact]
    public async Task GreaterThan_zero_yields_minimum_one_not_zero()
    {
        var schema = new OpenApiSchema
        {
            Properties = new Dictionary<string, IOpenApiSchema> { ["quantity"] = new OpenApiSchema { Type = JsonSchemaType.Integer } },
        };
        var services = new ServiceCollection().AddSingleton<IValidator<ZeroBound>>(new ZeroBoundValidator()).BuildServiceProvider();
        var context = new OpenApiSchemaTransformerContext
        {
            DocumentName = "v1",
            JsonTypeInfo = JsonTypeInfo.CreateJsonTypeInfo<ZeroBound>(JsonSerializerOptions.Default),
            JsonPropertyInfo = null,
            ParameterDescription = null,
            ApplicationServices = services,
        };

        await new FluentValidationSchemaTransformer().TransformAsync(schema, context, CancellationToken.None);

        var quantity = Assert.IsType<OpenApiSchema>(schema.Properties!["quantity"]);
        Assert.Equal("1", quantity.Minimum); // gt+1: il confine esclusivo→inclusivo
    }

    private sealed record OnlyMin(string Code);

    private sealed class OnlyMinValidator : AbstractValidator<OnlyMin>
    {
        // MinimumLength senza MaximumLength: length.Max == 0 → NON deve scrivere MaxLength.
        public OnlyMinValidator() => RuleFor(x => x.Code).MinimumLength(3);
    }

    [Fact]
    public async Task Minimum_length_only_does_not_emit_a_zero_max_length()
    {
        var schema = new OpenApiSchema
        {
            Properties = new Dictionary<string, IOpenApiSchema> { ["code"] = new OpenApiSchema { Type = JsonSchemaType.String } },
        };
        var services = new ServiceCollection().AddSingleton<IValidator<OnlyMin>>(new OnlyMinValidator()).BuildServiceProvider();
        var context = new OpenApiSchemaTransformerContext
        {
            DocumentName = "v1",
            JsonTypeInfo = JsonTypeInfo.CreateJsonTypeInfo<OnlyMin>(JsonSerializerOptions.Default),
            JsonPropertyInfo = null,
            ParameterDescription = null,
            ApplicationServices = services,
        };

        await new FluentValidationSchemaTransformer().TransformAsync(schema, context, CancellationToken.None);

        var code = Assert.IsType<OpenApiSchema>(schema.Properties!["code"]);
        Assert.Equal(3, code.MinLength);
        Assert.Null(code.MaxLength); // length.Max>0 è falso → niente MaxLength fittizio = 0
        Assert.Contains("min length 3", code.Description); // ma la descrizione c'è (constraints.Count>0)
    }

    [Fact]
    public async Task Types_without_a_registered_validator_are_skipped_entirely()
    {
        var schema = new OpenApiSchema
        {
            Properties = new Dictionary<string, IOpenApiSchema> { ["title"] = new OpenApiSchema() },
        };

        var context = new OpenApiSchemaTransformerContext
        {
            DocumentName = "v1",
            JsonTypeInfo = JsonTypeInfo.CreateJsonTypeInfo<Sample>(JsonSerializerOptions.Default),
            JsonPropertyInfo = null,
            ParameterDescription = null,
            ApplicationServices = new ServiceCollection().BuildServiceProvider(), // nessun IValidator<Sample>
        };

        await new FluentValidationSchemaTransformer().TransformAsync(schema, context, CancellationToken.None);

        var title = Assert.IsType<OpenApiSchema>(schema.Properties!["title"]);
        Assert.Null(title.MinLength);
    }
}
