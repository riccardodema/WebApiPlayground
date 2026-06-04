using WebApiPlayground.Application.Querying;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Interfaces;

public interface IBookRepository
{
    Task<(IReadOnlyList<Book> Items, int TotalCount)> GetPagedAsync(
        int pageNumber, int pageSize, BookSortField sortBy, SortDirection direction);
    Task<Book?> GetByIdAsync(int id);
    Task<Book> CreateAsync(Book book);

    /// <summary>Aggiorna un libro esistente con controllo di concorrenza ottimistica. Il token di
    /// versione atteso viaggia in <c>book.RowVersion</c> (usato come <c>OriginalValue</c> del concurrency
    /// token). Restituisce l'entità aggiornata (con autore) oppure <c>null</c> se nessun libro ha l'Id
    /// indicato; lancia <see cref="Concurrency.ConcurrencyConflictException"/> se la versione è stale.</summary>
    Task<Book?> UpdateAsync(Book book);

    /// <summary>Elimina un libro con controllo di concorrenza ottimistica: cancella solo se la
    /// <paramref name="expectedVersion"/> coincide con quella corrente. Restituisce <c>false</c> se il
    /// libro non esiste; lancia <see cref="Concurrency.ConcurrencyConflictException"/> se la versione è stale.</summary>
    Task<bool> DeleteAsync(int id, byte[] expectedVersion);
}
