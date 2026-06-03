using WebApiPlayground.Application.Querying;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Interfaces;

public interface IBookRepository
{
    Task<(IReadOnlyList<Book> Items, int TotalCount)> GetPagedAsync(
        int pageNumber, int pageSize, BookSortField sortBy, SortDirection direction);
    Task<Book?> GetByIdAsync(int id);
    Task<Book> CreateAsync(Book book);

    /// <summary>Aggiorna un libro esistente. Restituisce l'entità aggiornata (con autore)
    /// oppure <c>null</c> se nessun libro ha l'Id indicato.</summary>
    Task<Book?> UpdateAsync(Book book);
    Task<bool> DeleteAsync(int id);
}
