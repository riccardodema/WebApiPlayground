namespace WebApiPlayground.Application.DTOs;

/// <summary>
/// Payload per aggiornare un libro esistente (<c>PUT /api/books/{id}</c>).
/// La PUT sostituisce l'intera risorsa, quindi ha la stessa forma di <see cref="CreateBookDto"/>.
/// Le regole di validazione sono in <c>UpdateBookDtoValidator</c> e sono riportate nello schema OpenAPI.
/// </summary>
/// <param name="Title">Titolo del libro. Obbligatorio, da 1 a 100 caratteri (non solo spazi).</param>
/// <param name="AuthorId">Id di un autore esistente. Intero positivo (maggiore di 0).</param>
public record UpdateBookDto(string Title, int AuthorId);
