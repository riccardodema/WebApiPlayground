using FluentValidation;

namespace WebApiPlayground.Application.Validation;

/// <summary>
/// Regole di validazione condivise per i payload dei libri (Create/Update).
/// Centralizzarle qui evita che <c>CreateBookDto</c> e <c>UpdateBookDto</c> divergano
/// e tiene i limiti allineati allo schema del DB (es. <c>Books.Title NVARCHAR(100)</c>).
/// I messaggi sono "parlanti": dicono all'utente cosa è sbagliato e come correggerlo.
/// </summary>
public static class BookValidationRules
{
    /// <summary>Lunghezza massima del titolo, allineata alla colonna <c>Books.Title NVARCHAR(100)</c>.</summary>
    public const int TitleMaxLength = 100;

    /// <summary>Id autore minimo valido (gli IDENTITY partono da 1).</summary>
    public const int MinAuthorId = 1;

    public static IRuleBuilderOptions<T, string> ValidBookTitle<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty()
                .WithMessage("Title is required and cannot be empty or whitespace.")
            .MaximumLength(TitleMaxLength)
                .WithMessage($"Title must not exceed {TitleMaxLength} characters.");

    public static IRuleBuilderOptions<T, int> ValidAuthorId<T>(this IRuleBuilder<T, int> rule) =>
        rule
            .GreaterThan(0)
                .WithMessage("AuthorId must be a positive integer (greater than 0).");
}
