using System.Security.Claims;

namespace WebApiPlayground.Api.RateLimiting;

/// <summary>
/// Risolve la chiave di partizione (il "bucket") del rate limiter: il limite è per <b>client</b>,
/// non globale, così client diversi non si rubano la quota a vicenda. Utente autenticato →
/// <c>user:{id}</c> usando lo <b>stesso claim</b> della storage key dell'idempotency (<c>oid</c>
/// Entra, poi <c>NameIdentifier</c>), per coerenza; client anonimo → <c>ip:{indirizzo}</c>.
/// Nota: il rate limiter gira dopo l'autenticazione, quindi qui <see cref="HttpContext.User"/> è
/// già popolato. Vedi <c>.claude/context/rate-limiting.md</c>.
/// </summary>
public static class ClientPartition
{
    public static string ResolveKey(HttpContext context)
    {
        var userId =
            context.User.FindFirstValue("oid") ??
            context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!string.IsNullOrEmpty(userId))
            return $"user:{userId}";

        var ip = context.Connection.RemoteIpAddress?.ToString();
        return $"ip:{(string.IsNullOrEmpty(ip) ? "unknown" : ip)}";
    }
}
