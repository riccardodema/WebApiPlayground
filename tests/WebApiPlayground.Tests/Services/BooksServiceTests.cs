using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
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

    [Fact]
    public async Task GetAllBooksAsync_ReturnsMappedDtos_WhenBooksExist()
    {
        var books = new List<Book>
        {
            new() { Id = 1, Title = "Clean Code", AuthorId = 1, Author = new Author { Id = 1, FullName = "Robert C. Martin" } },
            new() { Id = 2, Title = "The Pragmatic Programmer", AuthorId = 2, Author = new Author { Id = 2, FullName = "Dave Thomas" } }
        };
        _repositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(books);

        var result = await _sut.GetAllBooksAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("Clean Code", result.First().Title);
        Assert.Equal("Robert C. Martin", result.First().AuthorFullName);
    }

    [Fact]
    public async Task GetAllBooksAsync_ReturnsEmptyCollection_WhenNoBooksExist()
    {
        _repositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Book>());

        var result = await _sut.GetAllBooksAsync();

        Assert.Empty(result);
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
    public async Task GetAllBooksAsync_MapsAuthorFullName_WhenAuthorIsNull()
    {
        var books = new List<Book>
        {
            new() { Id = 1, Title = "Orphan Book", AuthorId = 1, Author = null }
        };
        _repositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(books);

        var result = await _sut.GetAllBooksAsync();

        Assert.Equal(string.Empty, result.First().AuthorFullName);
    }
}
