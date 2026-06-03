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
    /// virgolette come richiede lo standard (es. <c>"9f86d0..."</c>).
    /// </summary>
    public static string Compute(ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, hash);
        return $"\"{Convert.ToHexStringLower(hash)}\"";
    }
}
