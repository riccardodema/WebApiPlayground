using System.Text.Json.Serialization;
using WebApiPlayground.Application.Concurrency;

namespace WebApiPlayground.Application.DTOs;

/// <summary>
/// Rappresentazione <b>v1</b> di un libro (autore come nome piatto). Porta il token di concorrenza
/// (<see cref="Version"/>) ma <b>fuori dal body</b>: è <c>[JsonIgnore]</c> e viaggia solo nell'header
/// <c>ETag</c> (vedi <see cref="IVersionedResource"/>). Il contratto JSON resta quindi invariato.
/// </summary>
public record BookDto(int Id, string Title, string AuthorFullName) : IVersionedResource
{
    /// <summary>Token di versione (base64 della rowversion), proiettato nell'ETag. Non serializzato nel body.</summary>
    [JsonIgnore]
    public string? Version { get; init; }
}
