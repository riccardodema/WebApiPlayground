using FluentValidation.TestHelper;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Validation;
using Xunit;

namespace WebApiPlayground.Tests.Validation;

public class CreateBookDtoValidatorTests
{
    private readonly CreateBookDtoValidator _sut = new();

    [Fact]
    public void Passes_WhenTitleAndAuthorIdAreValid()
    {
        var result = _sut.TestValidate(new CreateBookDto("Clean Code", 1));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenTitleIsEmptyOrWhitespace(string title)
    {
        var result = _sut.TestValidate(new CreateBookDto(title, 1));

        result.ShouldHaveValidationErrorFor(x => x.Title)
            .WithErrorMessage("Title is required and cannot be empty or whitespace.");
    }

    [Fact]
    public void Fails_WhenTitleExceedsMaxLength()
    {
        var title = new string('a', BookValidationRules.TitleMaxLength + 1);

        var result = _sut.TestValidate(new CreateBookDto(title, 1));

        result.ShouldHaveValidationErrorFor(x => x.Title)
            .WithErrorMessage($"Title must not exceed {BookValidationRules.TitleMaxLength} characters.");
    }

    [Fact]
    public void Passes_WhenTitleIsExactlyMaxLength()
    {
        var title = new string('a', BookValidationRules.TitleMaxLength);

        var result = _sut.TestValidate(new CreateBookDto(title, 1));

        result.ShouldNotHaveValidationErrorFor(x => x.Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Fails_WhenAuthorIdIsNotPositive(int authorId)
    {
        var result = _sut.TestValidate(new CreateBookDto("Valid Title", authorId));

        result.ShouldHaveValidationErrorFor(x => x.AuthorId)
            .WithErrorMessage("AuthorId must be a positive integer (greater than 0).");
    }

    [Fact]
    public void Fails_WithMultipleErrors_WhenBothFieldsInvalid()
    {
        var result = _sut.TestValidate(new CreateBookDto("", 0));

        result.ShouldHaveValidationErrorFor(x => x.Title);
        result.ShouldHaveValidationErrorFor(x => x.AuthorId);
    }
}
