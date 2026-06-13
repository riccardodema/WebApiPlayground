using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Application.Querying;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Repositories;
using WebApiPlayground.Tests.Persistence;
using Xunit;

namespace WebApiPlayground.Tests.Repositories;

/// <summary>
/// Comportamento del repository su un database relazionale reale (SQLite in-memory): la matematica
/// della paginazione, gli ordinamenti (incluso quello sull'entità correlata e il tiebreaker
/// deterministico), il caricamento dell'autore, e la scrittura transazionale libro+outbox.
/// I percorsi di conflitto di concorrenza (rowversion) restano agli integration test su SQL vero.
/// </summary>
public sealed class BookRepositoryTests : IDisposable
{
    private readonly SqlitePlaygroundDb _db = SqlitePlaygroundDb.Create();
    private readonly BookRepository _sut;

    public BookRepositoryTests()
    {
        _sut = new BookRepository(_db.Context, TimeProvider.System, NullLogger<BookRepository>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static IntegrationEvent Event(int bookId) => new PopularityEnrichmentRequested(bookId, null);

    private async Task SeedAsync(params (string Title, string Author)[] books)
    {
        var authors = books.Select(b => b.Author).Distinct()
            .ToDictionary(a => a, a => new Author { FullName = a });
        _db.Context.Authors.AddRange(authors.Values);
        foreach (var (title, author) in books)
            _db.Context.Books.Add(new Book { Title = title, Author = authors[author] });
        await _db.Context.SaveChangesAsync();
    }

    // ---- Paginazione: matematica e bordi ----------------------------------------

    [Fact]
    public async Task Paging_slices_the_set_and_reports_the_full_count()
    {
        await SeedAsync(("A", "X"), ("B", "X"), ("C", "X"), ("D", "X"), ("E", "X"));

        var (items, total) = await _sut.GetPagedAsync(2, 2, BookSortField.Title, SortDirection.Ascending);

        Assert.Equal(5, total);                       // il count è sull'INTERO set, non sulla pagina
        Assert.Equal(["C", "D"], items.Select(b => b.Title));
    }

    [Fact]
    public async Task Page_beyond_the_end_is_empty_but_count_is_still_right()
    {
        await SeedAsync(("A", "X"), ("B", "X"));

        var (items, total) = await _sut.GetPagedAsync(9, 10, BookSortField.Id, SortDirection.Ascending);

        Assert.Empty(items);
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task Empty_catalog_yields_empty_page_and_zero_count()
    {
        var (items, total) = await _sut.GetPagedAsync(1, 10, BookSortField.Id, SortDirection.Ascending);

        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    // ---- Ordinamenti -------------------------------------------------------------

    [Fact]
    public async Task Sort_by_title_descending_reverses_the_order()
    {
        await SeedAsync(("Alpha", "X"), ("Charlie", "X"), ("Bravo", "X"));

        var (items, _) = await _sut.GetPagedAsync(1, 10, BookSortField.Title, SortDirection.Descending);

        Assert.Equal(["Charlie", "Bravo", "Alpha"], items.Select(b => b.Title));
    }

    [Fact]
    public async Task Sort_by_author_orders_across_the_relationship()
    {
        await SeedAsync(("Dune", "Herbert"), ("Foundation", "Asimov"), ("Ubik", "Dick"));

        var (items, _) = await _sut.GetPagedAsync(1, 10, BookSortField.Author, SortDirection.Ascending);

        Assert.Equal(["Asimov", "Dick", "Herbert"], items.Select(b => b.Author!.FullName));
    }

    [Fact]
    public async Task Equal_sort_keys_break_ties_by_id_deterministically()
    {
        // Stesso titolo: senza tiebreaker l'ordine sarebbe libero → pagine instabili tra una
        // richiesta e l'altra (item saltati/duplicati cambiando pagina).
        await SeedAsync(("Same", "X"), ("Same", "X"), ("Same", "X"));

        var (firstPage, _) = await _sut.GetPagedAsync(1, 2, BookSortField.Title, SortDirection.Ascending);
        var (secondPage, _) = await _sut.GetPagedAsync(2, 2, BookSortField.Title, SortDirection.Ascending);

        var ids = firstPage.Concat(secondPage).Select(b => b.Id).ToList();
        Assert.Equal(ids.OrderBy(id => id).ToList(), ids); // unione delle pagine = sequenza per Id
        Assert.Equal(3, ids.Distinct().Count());           // nessun duplicato tra le pagine
    }

    [Theory]
    [InlineData(SortDirection.Ascending, new[] { "A", "B", "C" })]
    [InlineData(SortDirection.Descending, new[] { "C", "B", "A" })]
    public async Task Default_id_sort_honors_the_direction(SortDirection direction, string[] expected)
    {
        await SeedAsync(("A", "X"), ("B", "X"), ("C", "X")); // inseriti in ordine → Id crescenti

        var (items, _) = await _sut.GetPagedAsync(1, 10, BookSortField.Id, direction);

        Assert.Equal(expected, items.Select(b => b.Title));
    }

    [Fact]
    public async Task Create_with_a_nonexistent_author_surfaces_the_fk_violation()
    {
        // AuthorId sintatticamente valido ma inesistente: la FK deve fermarlo e l'eccezione
        // risalire (il GlobalExceptionHandler la traduce in 500 nel layer HTTP).
        await Assert.ThrowsAsync<DbUpdateException>(
            () => _sut.CreateAsync(new Book { Title = "Orphan", AuthorId = 999_999 }, Event));

        Assert.Equal(0, await _db.Context.OutboxMessages.CountAsync()); // niente outbox per una write fallita
    }

    [Fact]
    public async Task Paged_items_carry_their_author_loaded()
    {
        await SeedAsync(("Dune", "Herbert"));

        var (items, _) = await _sut.GetPagedAsync(1, 10, BookSortField.Id, SortDirection.Ascending);

        Assert.Equal("Herbert", items.Single().Author!.FullName); // niente lazy/N+1: già caricato
    }

    // ---- GetById -------------------------------------------------------------------

    [Fact]
    public async Task GetById_returns_the_book_with_its_author_or_null()
    {
        await SeedAsync(("Dune", "Herbert"));
        var id = (await _db.Context.Books.SingleAsync()).Id;

        var found = await _sut.GetByIdAsync(id);
        var missing = await _sut.GetByIdAsync(id + 999);

        Assert.Equal("Herbert", found!.Author!.FullName);
        Assert.Null(missing);
    }

    // ---- Scritture: libro + outbox nella stessa transazione --------------------------

    [Fact]
    public async Task Create_persists_the_book_and_an_outbox_row_with_the_generated_id()
    {
        await SeedAsync(("seed", "Herbert"));
        var authorId = (await _db.Context.Authors.SingleAsync()).Id;

        var created = await _sut.CreateAsync(new Book { Title = "Dune", AuthorId = authorId }, Event);

        Assert.True(created.Id > 0);
        Assert.Equal("Herbert", created.Author!.FullName); // l'entità torna già con l'autore

        using var fresh = _db.CreateFreshContext();
        var outbox = await fresh.OutboxMessages.SingleAsync();
        Assert.Equal(PopularityEnrichmentRequested.TypeName, outbox.Type);
        // La factory dell'evento è stata invocata con l'ID GENERATO dal database (chiave IDENTITY).
        Assert.Contains($"\"bookId\":{created.Id}", outbox.Payload);
        Assert.Null(outbox.ProcessedAt);
    }

    [Fact]
    public async Task Update_persists_changes_and_enqueues_a_new_outbox_row()
    {
        await SeedAsync(("Dune", "Herbert"));
        var book = await _db.Context.Books.AsNoTracking().SingleAsync();

        var updated = await _sut.UpdateAsync(
            new Book { Id = book.Id, Title = "Dune (rev)", AuthorId = book.AuthorId, RowVersion = book.RowVersion }, Event);

        Assert.Equal("Dune (rev)", updated!.Title);

        using var fresh = _db.CreateFreshContext();
        Assert.Equal("Dune (rev)", (await fresh.Books.SingleAsync()).Title);
        Assert.Equal(1, await fresh.OutboxMessages.CountAsync()); // anche l'update pubblica l'evento
    }

    [Fact]
    public async Task Update_of_a_missing_book_returns_null_and_writes_nothing()
    {
        var result = await _sut.UpdateAsync(new Book { Id = 12345, Title = "x", AuthorId = 1 }, Event);

        Assert.Null(result);
        Assert.Equal(0, await _db.Context.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Delete_removes_the_book_and_reports_whether_it_existed()
    {
        await SeedAsync(("Dune", "Herbert"));
        var book = await _db.Context.Books.AsNoTracking().SingleAsync();

        Assert.True(await _sut.DeleteAsync(book.Id, book.RowVersion));
        Assert.False(await _sut.DeleteAsync(book.Id, book.RowVersion)); // già rimosso

        Assert.Equal(0, await _db.Context.Books.CountAsync());
    }
}
