using FluentValidation;
using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Validation;

/// <summary>
/// Valida il payload di aggiornamento libro (<c>PUT /api/books/{id}</c>).
/// Condivide le regole con la creazione tramite <see cref="BookValidationRules"/>.
/// </summary>
public sealed class UpdateBookDtoValidator : AbstractValidator<UpdateBookDto>
{
    public UpdateBookDtoValidator()
    {
        RuleFor(x => x.Title).ValidBookTitle();
        RuleFor(x => x.AuthorId).ValidAuthorId();
    }
}
