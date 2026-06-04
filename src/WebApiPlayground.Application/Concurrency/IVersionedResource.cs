namespace WebApiPlayground.Application.Concurrency;

/// <summary>
/// Risorsa che espone un <b>token di versione</b> per l'optimistic concurrency. Il token è l'impronta
/// opaca della versione corrente della risorsa (qui: la <c>rowversion</c> del DB, codificata base64).
///
/// <para>Il layer HTTP lo proietta nell'header <c>ETag</c> (un solo token serve sia il caching
/// condizionale, <c>304</c>, sia la concorrenza, <c>412/428</c> via <c>If-Match</c>). Il token NON
/// compare nel body (la proprietà è <c>[JsonIgnore]</c> sui DTO): l'header è il canale canonico
/// (RFC 9110/7232).</para>
/// </summary>
public interface IVersionedResource
{
    /// <summary>Token di versione opaco (base64 della rowversion), oppure <c>null</c> se non disponibile.</summary>
    string? Version { get; }
}
