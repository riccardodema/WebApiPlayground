using FluentValidation.TestHelper;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Validation;
using Xunit;

namespace WebApiPlayground.Tests.Validation;

public class UpdateBookDtoValidatorTests
{
    private readonly UpdateBookDtoValidator _sut = new();

    [Fact]
    public void Passes_WhenTitleAndAuthorIdAreValid()
    {
        var result = _sut.TestValidate(new UpdateBookDto("The Pragmatic Programmer", 2));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenTitleIsEmptyOrWhitespace(string title)
    {
        var result = _sut.TestValidate(new UpdateBookDto(title, 1));

        result.ShouldHaveValidationErrorFor(x => x.Title)
            .WithErrorMessage("Title is required and cannot be empty or whitespace.");
    }

    [Fact]
    public void Fails_WhenTitleExceedsMaxLength()
    {
        var title = new string('a', BookValidationRules.TitleMaxLength + 1);

        var result = _sut.TestValidate(new UpdateBookDto(title, 1));

        result.ShouldHaveValidationErrorFor(x => x.Title)
            .WithErrorMessage($"Title must not exceed {BookValidationRules.TitleMaxLength} characters.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Fails_WhenAuthorIdIsNotPositive(int authorId)
    {
        var result = _sut.TestValidate(new UpdateBookDto("Valid Title", authorId));

        result.ShouldHaveValidationErrorFor(x => x.AuthorId)
            .WithErrorMessage("AuthorId must be a positive integer (greater than 0).");
    }
}
