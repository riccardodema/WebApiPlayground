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
