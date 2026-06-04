namespace WebApiPlayground.Application.Idempotency;

/// <summary>
/// Risposta memorizzata per una <c>Idempotency-Key</c>: tutto ciò che serve per rigiocarla
/// <i>verbatim</i> a un ritentativo con la stessa chiave. Tipo "puro" (nessuna dipendenza HTTP)
/// così vive nel layer Application; lo store la serializza. Vedi <c>.claude/context/idempotency.md</c>.
/// </summary>
/// <param name="StatusCode">Status HTTP della prima risposta (es. 201).</param>
/// <param name="Location">Header <c>Location</c> della prima risposta, se presente (es. per 201 Created).</param>
/// <param name="ContentType">Content-Type del body memorizzato.</param>
/// <param name="Body">Body della prima risposta (testo: i nostri payload sono JSON UTF-8).</param>
/// <param name="RequestFingerprint">
/// Impronta (hash) del corpo della richiesta originale: a un ritentativo con la stessa chiave ma
/// payload diverso si risponde 422 invece di rigiocare la risposta sbagliata.
/// </param>
public sealed record IdempotencyRecord(
    int StatusCode,
    string? Location,
    string ContentType,
    string Body,
    string RequestFingerprint);
