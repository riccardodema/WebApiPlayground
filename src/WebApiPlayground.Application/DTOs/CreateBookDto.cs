namespace WebApiPlayground.Application.DTOs;

/// <summary>
/// Payload per creare un nuovo libro (<c>POST /api/books</c>).
/// Le regole di validazione sono in <c>CreateBookDtoValidator</c> e sono riportate
/// nello schema OpenAPI (campi <c>required</c>, <c>maxLength</c>, <c>minimum</c>).
/// </summary>
/// <param name="Title">Titolo del libro. Obbligatorio, da 1 a 100 caratteri (non solo spazi).</param>
/// <param name="AuthorId">Id di un autore esistente. Intero positivo (maggiore di 0).</param>
public record CreateBookDto(string Title, int AuthorId);
