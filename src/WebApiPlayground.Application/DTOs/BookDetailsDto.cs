namespace WebApiPlayground.Application.DTOs;

/// <summary>
/// Rappresentazione <b>v2</b> di un libro: l'autore è un oggetto annidato (<see cref="AuthorDto"/>)
/// invece del nome piatto di <see cref="BookDto"/> (<c>AuthorFullName</c>). È un breaking change
/// sulla forma della risposta → il motivo da manuale per cui si versiona l'API. La fetch dei dati è
/// la stessa di v1 (cambia solo la proiezione). Vedi <c>.claude/context/api-versioning.md</c>.
/// </summary>
public record BookDetailsDto(int Id, string Title, AuthorDto Author);
