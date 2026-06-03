using FluentValidation;
using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Validation;

/// <summary>
/// Valida il payload di creazione libro (<c>POST /api/books</c>).
/// Una violazione produce un 400 <c>application/problem+json</c> (vedi <c>ValidationFilter</c>).
/// </summary>
public sealed class CreateBookDtoValidator : AbstractValidator<CreateBookDto>
{
    public CreateBookDtoValidator()
    {
        RuleFor(x => x.Title).ValidBookTitle();
        RuleFor(x => x.AuthorId).ValidAuthorId();
    }
}
