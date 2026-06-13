using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Querying;
using WebApiPlayground.Application.Services;
using WebApiPlayground.Domain.Entities;
using Xunit;

namespace WebApiPlayground.Tests.Services;

/// <summary>
/// Edge del service: il fallback dell'ordinamento per <c>sortBy</c> sconosciuto (whitelist, mai
/// un'eccezione né un passthrough al DB) e la codifica del token di versione nell'ETag
/// (base64 della rowversion; assente ⇒ null, MAI stringa vuota — il layer HTTP distingue i due casi).
/// </summary>
public class BooksServiceEdgeTests
{
    private readonly Mock<IBookRepository> _repository = new();
    private readonly BooksService _sut;

    public BooksServiceEdgeTests()
    {
        _sut = new BooksService(_repository.Object, NullLogger<BooksService>.Instance);
        _repository
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync(([], 0));
    }

    [Fact]
    public async Task Unknown_sort_field_falls_back_to_the_default_id_sort()
    {
        await _sut.GetBooksAsync(new BooksQueryParameters { SortBy = "prezzo-al-kilo", SortDir = "asc" });

        // La whitelist protegge il DB: input fuori vocabolario → ordinamento di default, non errore.
        _repository.Verify(r => r.GetPagedAsync(
            It.IsAny<int>(), It.IsAny<int>(), BookSortField.Id, SortDirection.Ascending), Times.Once);
    }

    [Fact]
    public async Task Known_sort_field_is_passed_through_to_the_repository()
    {
        await _sut.GetBooksAsync(new BooksQueryParameters { SortBy = "title", SortDir = "desc" });

        _repository.Verify(r => r.GetPagedAsync(
            It.IsAny<int>(), It.IsAny<int>(), BookSortField.Title, SortDirection.Descending), Times.Once);
    }

    [Fact]
    public async Task Version_token_is_the_base64_of_the_rowversion()
    {
        var rowVersion = new byte[] { 1, 2, 3, 4 };
        _repository.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(
            new Book { Id = 1, Title = "Dune", Author = new Author { FullName = "Herbert" }, RowVersion = rowVersion });

        var dto = await _sut.GetBookByIdAsync(1);

        Assert.Equal(Convert.ToBase64String(rowVersion), dto!.Version);
    }

    [Fact]
    public async Task Missing_rowversion_yields_a_null_token_not_an_empty_string()
    {
        _repository.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(
            new Book { Id = 1, Title = "Dune", Author = new Author { FullName = "Herbert" }, RowVersion = [] });

        var dto = await _sut.GetBookByIdAsync(1);

        // null ⇒ il filtro HTTP ricade sull'ETag per-rappresentazione; "" produrrebbe un ETag malformato.
        Assert.Null(dto!.Version);
    }

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new BooksService(null!, NullLogger<BooksService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new BooksService(_repository.Object, null!));
    }
}
