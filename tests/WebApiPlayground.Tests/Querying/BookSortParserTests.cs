using Xunit;
using WebApiPlayground.Application.Querying;

namespace WebApiPlayground.Tests.Querying;

public class BookSortParserTests
{
    [Theory]
    [InlineData("id", BookSortField.Id)]
    [InlineData("title", BookSortField.Title)]
    [InlineData("author", BookSortField.Author)]
    [InlineData("TITLE", BookSortField.Title)]   // case-insensitive
    [InlineData("  author  ", BookSortField.Author)] // trim
    public void TryParseField_ReturnsTrueAndMaps_ForKnownValues(string input, BookSortField expected)
    {
        var ok = BookSortParser.TryParseField(input, out var field);

        Assert.True(ok);
        Assert.Equal(expected, field);
    }

    [Theory]
    [InlineData("xxx")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("DROP TABLE")]
    public void TryParseField_ReturnsFalseAndDefault_ForUnknownValues(string? input)
    {
        var ok = BookSortParser.TryParseField(input, out var field);

        Assert.False(ok);
        Assert.Equal(BookSortParser.DefaultField, field);
        Assert.Equal(BookSortField.Id, field);
    }

    [Theory]
    [InlineData("desc")]
    [InlineData("DESC")]
    [InlineData("  Desc  ")]
    public void ParseDirection_ReturnsDescending_ForDesc(string input)
    {
        Assert.Equal(SortDirection.Descending, BookSortParser.ParseDirection(input));
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("ascending")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void ParseDirection_ReturnsAscending_ForAnythingElse(string? input)
    {
        Assert.Equal(SortDirection.Ascending, BookSortParser.ParseDirection(input));
    }
}
