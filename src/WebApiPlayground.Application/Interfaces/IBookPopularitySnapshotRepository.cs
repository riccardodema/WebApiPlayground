using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Interfaces;

/// <summary>
/// Accesso allo store durevole degli snapshot di popolarità (tabella <c>BookPopularitySnapshots</c>, 1:1 col
/// libro). Lo scrive il worker di arricchimento (<see cref="UpsertAsync"/>); lo legge il read endpoint come
/// fallback d'outage (<see cref="GetByBookIdAsync"/>). Restituisce entità di dominio: i dettagli EF restano in
/// Infrastructure. Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
public interface IBookPopularitySnapshotRepository
{
    /// <summary>Carica lo snapshot di un libro, oppure <c>null</c> se non è mai stato arricchito.</summary>
    Task<BookPopularitySnapshot?> GetByBookIdAsync(int bookId, CancellationToken cancellationToken);

    /// <summary>Inserisce o aggiorna (per <c>BookId</c>) lo snapshot di popolarità.</summary>
    Task UpsertAsync(BookPopularitySnapshot snapshot, CancellationToken cancellationToken);
}
