using FluentValidation;
using FluentValidation.Validators;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Riporta le regole FluentValidation nello schema OpenAPI generato, così il contratto
/// è "parlante" anche sui vincoli di input (<c>required</c>, <c>maxLength</c>, <c>minLength</c>,
/// <c>minimum</c>) e su una descrizione leggibile per ogni campo — senza duplicare le regole:
/// i validator restano l'unica fonte di verità, qui vengono solo proiettati.
/// </summary>
public sealed class FluentValidationSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema.Properties is not { Count: > 0 })
            return Task.CompletedTask;

        var validatorType = typeof(IValidator<>).MakeGenericType(context.JsonTypeInfo.Type);
        if (context.ApplicationServices.GetService(validatorType) is not IValidator validator)
            return Task.CompletedTask;

        var descriptor = validator.CreateDescriptor();

        foreach (var (propertyKey, propertySchemaInterface) in schema.Properties)
        {
            if (propertySchemaInterface is not OpenApiSchema propertySchema)
                continue;

            var validators = descriptor
                .GetMembersWithValidators()
                .FirstOrDefault(g => string.Equals(g.Key, propertyKey, StringComparison.OrdinalIgnoreCase));
            if (validators is null)
                continue;

            ApplyRules(schema, propertyKey, propertySchema, validators.Select(v => v.Validator));
        }

        return Task.CompletedTask;
    }

    private static void ApplyRules(
        OpenApiSchema parent, string propertyKey, OpenApiSchema property, IEnumerable<IPropertyValidator> validators)
    {
        var constraints = new List<string>();

        foreach (var v in validators)
        {
            switch (v)
            {
                case INotEmptyValidator or INotNullValidator:
                    parent.Required ??= new HashSet<string>();
                    parent.Required.Add(propertyKey);
                    if (IsStringSchema(property) && (property.MinLength is null || property.MinLength < 1))
                        property.MinLength = 1;
                    constraints.Add("required");
                    break;

                case ILengthValidator length:
                    if (length.Max > 0)
                    {
                        property.MaxLength = length.Max;
                        constraints.Add($"max length {length.Max}");
                    }
                    if (length.Min > 0)
                    {
                        property.MinLength = length.Min;
                        constraints.Add($"min length {length.Min}");
                    }
                    break;

                case IComparisonValidator { Comparison: Comparison.GreaterThan } cmp
                    when TryToLong(cmp.ValueToCompare, out var gt):
                    property.Minimum = (gt + 1).ToString();
                    constraints.Add($"minimum {gt + 1}");
                    break;

                case IComparisonValidator { Comparison: Comparison.GreaterThanOrEqual } cmp
                    when TryToLong(cmp.ValueToCompare, out var gte):
                    property.Minimum = gte.ToString();
                    constraints.Add($"minimum {gte}");
                    break;
            }
        }

        if (constraints.Count > 0)
        {
            var summary = $"Validation: {string.Join("; ", constraints)}.";
            property.Description = string.IsNullOrWhiteSpace(property.Description)
                ? summary
                : $"{property.Description.TrimEnd()} {summary}";
        }
    }

    private static bool IsStringSchema(OpenApiSchema schema) =>
        schema.Type is null || schema.Type.Value.HasFlag(JsonSchemaType.String);

    private static bool TryToLong(object? value, out long result)
    {
        if (value is not null && value is IConvertible)
        {
            try
            {
                result = Convert.ToInt64(value);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // Valore di confronto non numerico (es. GreaterThan su una data): nessun vincolo numerico.
            }
        }

        result = 0;
        return false;
    }
}
