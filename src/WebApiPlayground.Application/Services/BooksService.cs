using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.BackgroundProcessing;
using WebApiPlayground.Application.Diagnostics;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Querying;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Services;

public class BooksService : IBooksService
{
    private readonly IBookRepository _repository;
    private readonly IBackgroundTaskQueue<PopularityEnrichmentRequest> _enrichmentQueue;
    private readonly ILogger<BooksService> _logger;

    public BooksService(
        IBookRepository repository,
        IBackgroundTaskQueue<PopularityEnrichmentRequest> enrichmentQueue,
        ILogger<BooksService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(enrichmentQueue);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _enrichmentQueue = enrichmentQueue;
        _logger = logger;
    }

    public async Task<PagedResult<BookDto>> GetBooksAsync(BooksQueryParameters query)
    {
        var (books, totalCount) = await GetPagedBooksAsync(query);

        var items = books.Select(MapToDto).ToList();

        _logger.LogDebug(
            "Mapped {BookCount} of {TotalCount} book(s) to DTO", items.Count, totalCount);

        return new PagedResult<BookDto>(items, query.PageNumber, query.PageSize, totalCount);
    }

    /// <summary>
    /// Paginazione + ordinamento condivisi dalle letture v1 e v2 (DRY): cambia solo la proiezione
    /// finale (MapToDto vs MapToDetailsDto), non il modo di interrogare il repository.
    /// </summary>
    private async Task<(IReadOnlyList<Book> Books, int TotalCount)> GetPagedBooksAsync(BooksQueryParameters query)
    {
        // Il vocabolario delle stringhe di sort vive solo in BookSortParser (whitelist type-safe).
        if (!BookSortParser.TryParseField(query.SortBy, out var sortField))
            _logger.LogWarning(
                "Unknown sortBy '{RequestedSortBy}' — falling back to {DefaultSort}",
                query.SortBy, BookSortParser.DefaultField);

        var direction = BookSortParser.ParseDirection(query.SortDir);

        _logger.LogDebug(
            "Retrieving books page {PageNumber} (size {PageSize}), sort {SortBy} {SortDir}",
            query.PageNumber, query.PageSize, sortField, direction);

        return await _repository.GetPagedAsync(query.PageNumber, query.PageSize, sortField, direction);
    }

    public async Task<BookDto?> GetBookByIdAsync(int id)
    {
        _logger.LogDebug("Looking up book {BookId} in repository", id);

        var book = await _repository.GetByIdAsync(id);

        if (book is null)
        {
            _logger.LogDebug("Repository returned no book for ID {BookId}", id);
            return null;
        }

        _logger.LogDebug("Book {BookId} retrieved: '{BookTitle}' by {AuthorName}",
            id, book.Title, book.Author?.FullName ?? "unknown author");

        return MapToDto(book);
    }

    public async Task<BookDto> CreateBookAsync(CreateBookDto dto)
    {
        // Span di business custom: misura la creazione nel waterfall della trace, annidato sotto lo span
        // HTTP server. null (zero overhead) se nessun listener OTel è in ascolto. Vedi BooksDiagnostics.
        using var activity = BooksDiagnostics.StartCreateBookActivity();
        activity?.SetTag("book.author_id", dto.AuthorId);

        _logger.LogDebug(
            "Building book entity — Title: '{BookTitle}', AuthorId: {AuthorId}",
            dto.Title, dto.AuthorId);

        var book = new Book { Title = dto.Title, AuthorId = dto.AuthorId };
        var created = await _repository.CreateAsync(book);

        // Metrica di dominio: contatore dei libri creati con successo (serie temporale per dashboard/alert).
        activity?.SetTag("book.id", created.Id);
        BooksDiagnostics.RecordBookCreated();

        _logger.LogDebug(
            "Book entity persisted with ID {BookId}, author resolved as '{AuthorName}'",
            created.Id, created.Author?.FullName ?? "unknown");

        // Arricchimento popolarità fuori dal path di scrittura: la chiamata esterna (lenta) la fa il worker.
        EnqueuePopularityEnrichment(created.Id);

        return MapToDto(created);
    }

    public async Task<BookDto?> UpdateBookAsync(int id, UpdateBookDto dto, byte[] expectedVersion)
    {
        _logger.LogDebug(
            "Updating book {BookId} — new Title: '{BookTitle}', AuthorId: {AuthorId}",
            id, dto.Title, dto.AuthorId);

        // Il token atteso (If-Match) viaggia in RowVersion: il repository lo usa come OriginalValue del
        // concurrency token, così l'UPDATE è condizionale (WHERE Id=@id AND RowVersion=@expectedVersion).
        var book = new Book { Id = id, Title = dto.Title, AuthorId = dto.AuthorId, RowVersion = expectedVersion };
        var updated = await _repository.UpdateAsync(book);

        if (updated is null)
        {
            _logger.LogDebug("Repository reported book {BookId} does not exist — nothing updated", id);
            return null;
        }

        _logger.LogDebug(
            "Book {BookId} updated, author resolved as '{AuthorName}'",
            updated.Id, updated.Author?.FullName ?? "unknown");

        // Titolo/autore possono essere cambiati → la popolarità va ricalcolata in background.
        EnqueuePopularityEnrichment(updated.Id);

        return MapToDto(updated);
    }

    public async Task<bool> DeleteBookAsync(int id, byte[] expectedVersion)
    {
        _logger.LogDebug("Requesting deletion of book {BookId} from repository", id);

        var deleted = await _repository.DeleteAsync(id, expectedVersion);

        if (!deleted)
            _logger.LogDebug("Repository reported book {BookId} does not exist — nothing deleted", id);
        else
            _logger.LogDebug("Repository confirmed book {BookId} has been deleted", id);

        return deleted;
    }

    public async Task<PagedResult<BookDetailsDto>> GetBooksDetailedAsync(BooksQueryParameters query)
    {
        var (books, totalCount) = await GetPagedBooksAsync(query);

        var items = books.Select(MapToDetailsDto).ToList();

        _logger.LogDebug(
            "Mapped {BookCount} of {TotalCount} book(s) to detailed (v2) DTO", items.Count, totalCount);

        return new PagedResult<BookDetailsDto>(items, query.PageNumber, query.PageSize, totalCount);
    }

    public async Task<BookDetailsDto?> GetBookDetailsByIdAsync(int id)
    {
        _logger.LogDebug("Looking up book {BookId} in repository (v2 shape)", id);

        var book = await _repository.GetByIdAsync(id);

        return book is null ? null : MapToDetailsDto(book);
    }

    /// <summary>
    /// Accoda (best-effort, non bloccante) l'arricchimento popolarità per un libro. Cattura il
    /// <see cref="Activity.Current"/> così lo span del worker si aggancia alla trace di questa write
    /// (correlazione oltre il confine async). Coda piena → si scarta loggando: la write resta veloce
    /// (il drop è osservato come metrica nella coda). Vedi <c>.claude/context/background-processing.md</c>.
    /// </summary>
    private void EnqueuePopularityEnrichment(int bookId)
    {
        var request = new PopularityEnrichmentRequest(bookId, Activity.Current?.Context ?? default);

        if (!_enrichmentQueue.TryEnqueue(request))
            _logger.LogWarning(
                "Popularity enrichment queue is full — dropping enrichment for book {BookId}", bookId);
    }

    private static BookDto MapToDto(Book book) =>
        new(book.Id, book.Title, book.Author?.FullName ?? string.Empty) { Version = EncodeVersion(book.RowVersion) };

    // Proiezione v2: l'autore diventa un oggetto annidato (Id + FullName) invece del nome piatto.
    private static BookDetailsDto MapToDetailsDto(Book book) =>
        new(book.Id, book.Title, new AuthorDto(book.Author?.Id ?? 0, book.Author?.FullName ?? string.Empty))
        { Version = EncodeVersion(book.RowVersion) };

    // Token di concorrenza opaco esposto come ETag: base64 della rowversion. Vuoto/null se assente
    // (es. entità non ancora persistita) → il layer HTTP ricade sull'ETag per-rappresentazione.
    private static string? EncodeVersion(byte[] rowVersion) =>
        rowVersion is { Length: > 0 } ? Convert.ToBase64String(rowVersion) : null;
}
