using System.Security.Cryptography;

namespace WebApiPlayground.Api.Http;

/// <summary>
/// Calcolo dell'ETag (RFC 9110) di una rappresentazione. Funzione pura e deterministica:
/// stesso payload ⇒ stesso ETag, payload diverso ⇒ ETag diverso (con probabilità di
/// collisione trascurabile, SHA-256). Estratto a parte per poterlo unit-testare.
/// </summary>
public static class ETag
{
    /// <summary>
    /// ETag <b>strong</b> dai byte della rappresentazione: SHA-256 → hex minuscolo, racchiuso tra
    /// virgolette come richiede lo standard (es. <c>"9f86d0..."</c>). Usato per le risorse senza un
    /// token di versione proprio (es. le liste paginate): l'ETag serve solo al caching condizionale.
    /// </summary>
    public static string Compute(ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, hash);
        return $"\"{Convert.ToHexStringLower(hash)}\"";
    }

    /// <summary>
    /// ETag <b>strong</b> a partire da un token di versione opaco (qui: la rowversion in base64):
    /// lo racchiude tra virgolette. A differenza di <see cref="Compute"/> è <b>reversibile</b>
    /// (<see cref="TryParseToken"/>), così l'<c>If-Match</c> ricevuto può tornare ai byte della
    /// rowversion da usare come concurrency token EF Core.
    /// </summary>
    public static string FromVersion(string versionToken) => $"\"{versionToken}\"";

    /// <summary>
    /// Inverso di <see cref="FromVersion"/>: estrae da un header <c>If-Match</c> (ETag strong quotato,
    /// con eventuale prefisso debole <c>W/</c>) i byte del token di versione. Restituisce <c>false</c>
    /// se il valore è assente, non quotato o non è base64 valido (→ il chiamante risponde 400).
    /// </summary>
    public static bool TryParseToken(string? ifMatch, out byte[] token)
    {
        token = [];
        if (string.IsNullOrWhiteSpace(ifMatch))
            return false;

        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..];

        // Un ETag strong è sempre racchiuso tra virgolette: senza, è malformato.
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return false;
        value = value[1..^1];

        try
        {
            token = Convert.FromBase64String(value);
            return token.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
