using System.Text.Json.Serialization;
using WebApiPlayground.Application.Concurrency;

namespace WebApiPlayground.Application.DTOs;

/// <summary>
/// Rappresentazione <b>v2</b> di un libro: l'autore è un oggetto annidato (<see cref="AuthorDto"/>)
/// invece del nome piatto di <see cref="BookDto"/> (<c>AuthorFullName</c>). È un breaking change
/// sulla forma della risposta → il motivo da manuale per cui si versiona l'API. La fetch dei dati è
/// la stessa di v1 (cambia solo la proiezione). Vedi <c>.claude/context/api-versioning.md</c>.
///
/// <para>Come v1 porta il token di concorrenza (<see cref="Version"/>) fuori dal body, solo nell'ETag.</para>
/// </summary>
public record BookDetailsDto(int Id, string Title, AuthorDto Author) : IVersionedResource
{
    /// <summary>Token di versione (base64 della rowversion), proiettato nell'ETag. Non serializzato nel body.</summary>
    [JsonIgnore]
    public string? Version { get; init; }
}
