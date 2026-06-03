using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Querying;
using WebApiPlayground.Application.Services;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Tests.Services;

public class BooksServiceTests
{
    private readonly Mock<IBookRepository> _repositoryMock = new();
    private readonly BooksService _sut;

    public BooksServiceTests()
    {
        _sut = new BooksService(_repositoryMock.Object, NullLogger<BooksService>.Instance);
    }

    private static BooksQueryParameters Query(
        int pageNumber = 1, int pageSize = 20, string sortBy = "id", string sortDir = "asc") =>
        new() { PageNumber = pageNumber, PageSize = pageSize, SortBy = sortBy, SortDir = sortDir };

    [Fact]
    public async Task GetBooksAsync_ReturnsMappedDtos_WhenBooksExist()
    {
        var books = new List<Book>
        {
            new() { Id = 1, Title = "Clean Code", AuthorId = 1, Author = new Author { Id = 1, FullName = "Robert C. Martin" } },
            new() { Id = 2, Title = "The Pragmatic Programmer", AuthorId = 2, Author = new Author { Id = 2, FullName = "Dave Thomas" } }
        };
        _repositoryMock
            .Setup(r => r.GetPagedAsync(1, 20, BookSortField.Id, SortDirection.Ascending))
            .ReturnsAsync((books, 2));

        var result = await _sut.GetBooksAsync(Query());

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Clean Code", result.Items.First().Title);
        Assert.Equal("Robert C. Martin", result.Items.First().AuthorFullName);
    }

    [Fact]
    public async Task GetBooksAsync_ReturnsEmptyPage_WhenNoBooksExist()
    {
        _repositoryMock
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync((new List<Book>(), 0));

        var result = await _sut.GetBooksAsync(Query());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.TotalPages);
        Assert.False(result.HasNext);
        Assert.False(result.HasPrevious);
    }

    [Fact]
    public async Task GetBooksAsync_ComputesPagingMetadata()
    {
        // page 2 di 7 con 127 elementi totali, size 20
        _repositoryMock
            .Setup(r => r.GetPagedAsync(2, 20, BookSortField.Id, SortDirection.Ascending))
            .ReturnsAsync((new List<Book> { new() { Id = 21, Title = "X", Author = new Author { FullName = "A" } } }, 127));

        var result = await _sut.GetBooksAsync(Query(pageNumber: 2));

        Assert.Equal(2, result.PageNumber);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(127, result.TotalCount);
        Assert.Equal(7, result.TotalPages);
        Assert.True(result.HasPrevious);
        Assert.True(result.HasNext);
    }

    [Fact]
    public async Task GetBooksAsync_MapsSortByTitleDesc_ToEnums()
    {
        _repositoryMock
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync((new List<Book>(), 0));

        await _sut.GetBooksAsync(Query(sortBy: "title", sortDir: "DESC"));

        _repositoryMock.Verify(
            r => r.GetPagedAsync(1, 20, BookSortField.Title, SortDirection.Descending), Times.Once);
    }

    [Fact]
    public async Task GetBooksAsync_FallsBackToId_WhenSortByNotAllowed()
    {
        _repositoryMock
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync((new List<Book>(), 0));

        await _sut.GetBooksAsync(Query(sortBy: "DROP TABLE"));

        _repositoryMock.Verify(
            r => r.GetPagedAsync(1, 20, BookSortField.Id, SortDirection.Ascending), Times.Once);
    }

    [Fact]
    public async Task GetBookByIdAsync_ReturnsDto_WhenBookExists()
    {
        var book = new Book { Id = 1, Title = "Clean Code", AuthorId = 1, Author = new Author { Id = 1, FullName = "Robert C. Martin" } };
        _repositoryMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(book);

        var result = await _sut.GetBookByIdAsync(1);

        Assert.NotNull(result);
        Assert.Equal(1, result.Id);
        Assert.Equal("Clean Code", result.Title);
        Assert.Equal("Robert C. Martin", result.AuthorFullName);
    }

    [Fact]
    public async Task GetBookByIdAsync_ReturnsNull_WhenBookNotFound()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Book?)null);

        var result = await _sut.GetBookByIdAsync(999);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateBookAsync_ReturnsMappedDto_WhenCreated()
    {
        var dto = new CreateBookDto("Clean Code", 1);
        var createdBook = new Book { Id = 1, Title = "Clean Code", AuthorId = 1, Author = new Author { Id = 1, FullName = "Robert C. Martin" } };
        _repositoryMock
            .Setup(r => r.CreateAsync(It.Is<Book>(b => b.Title == dto.Title && b.AuthorId == dto.AuthorId)))
            .ReturnsAsync(createdBook);

        var result = await _sut.CreateBookAsync(dto);

        Assert.Equal(1, result.Id);
        Assert.Equal("Clean Code", result.Title);
        Assert.Equal("Robert C. Martin", result.AuthorFullName);
    }

    [Fact]
    public async Task DeleteBookAsync_ReturnsTrue_WhenBookExists()
    {
        _repositoryMock.Setup(r => r.DeleteAsync(1)).ReturnsAsync(true);

        var result = await _sut.DeleteBookAsync(1);

        Assert.True(result);
        _repositoryMock.Verify(r => r.DeleteAsync(1), Times.Once);
    }

    [Fact]
    public async Task DeleteBookAsync_ReturnsFalse_WhenBookNotFound()
    {
        _repositoryMock.Setup(r => r.DeleteAsync(999)).ReturnsAsync(false);

        var result = await _sut.DeleteBookAsync(999);

        Assert.False(result);
    }

    [Fact]
    public async Task GetBooksAsync_MapsAuthorFullName_WhenAuthorIsNull()
    {
        var books = new List<Book>
        {
            new() { Id = 1, Title = "Orphan Book", AuthorId = 1, Author = null }
        };
        _repositoryMock
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync((books, 1));

        var result = await _sut.GetBooksAsync(Query());

        Assert.Equal(string.Empty, result.Items.First().AuthorFullName);
    }
}
