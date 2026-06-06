using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Application.Querying;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Interfaces;

public interface IBookRepository
{
    Task<(IReadOnlyList<Book> Items, int TotalCount)> GetPagedAsync(
        int pageNumber, int pageSize, BookSortField sortBy, SortDirection direction);
    Task<Book?> GetByIdAsync(int id);

    /// <summary>Crea un libro e scrive, <b>nella stessa transazione</b>, l'evento di integrazione prodotto
    /// da <paramref name="outboxEvent"/> (transactional outbox): o committano insieme, o rollback insieme.
    /// La factory riceve l'Id store-generated assegnato dopo l'INSERT, così il payload può riferirlo.</summary>
    Task<Book> CreateAsync(Book book, Func<int, IntegrationEvent> outboxEvent);

    /// <summary>Aggiorna un libro esistente con controllo di concorrenza ottimistica, scrivendo l'evento di
    /// integrazione di <paramref name="outboxEvent"/> nella <b>stessa transazione</b> (transactional outbox).
    /// Il token di versione atteso viaggia in <c>book.RowVersion</c> (usato come <c>OriginalValue</c> del
    /// concurrency token). Restituisce l'entità aggiornata (con autore) oppure <c>null</c> se nessun libro ha
    /// l'Id indicato (in tal caso nessun evento è scritto); lancia
    /// <see cref="Concurrency.ConcurrencyConflictException"/> se la versione è stale.</summary>
    Task<Book?> UpdateAsync(Book book, Func<int, IntegrationEvent> outboxEvent);

    /// <summary>Elimina un libro con controllo di concorrenza ottimistica: cancella solo se la
    /// <paramref name="expectedVersion"/> coincide con quella corrente. Restituisce <c>false</c> se il
    /// libro non esiste; lancia <see cref="Concurrency.ConcurrencyConflictException"/> se la versione è stale.</summary>
    Task<bool> DeleteAsync(int id, byte[] expectedVersion);
}
