namespace WebApiPlayground.Infrastructure.Caching;

/// <summary>
/// Opzioni della cache, bindate dalla sezione <c>Cache</c> della configurazione. Tutte hanno
/// default sensati: senza configurazione la cache è in-memory (solo L1). Valorizzando
/// <see cref="RedisSettings.ConnectionString"/> si attivano L2 (Redis) + backplane senza
/// toccare il codice — vedi <c>.claude/context/caching.md</c>.
/// </summary>
public sealed class CacheSettings
{
    public const string SectionName = "Cache";

    /// <summary>Durata di freschezza di una entry (poi viene rinfrescata; vedi fail-safe).</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Finestra entro cui, in fail-safe, si può servire un valore scaduto se la factory fallisce.</summary>
    public TimeSpan FailSafeMaxDuration { get; init; } = TimeSpan.FromHours(2);

    public RedisSettings Redis { get; init; } = new();

    public sealed class RedisSettings
    {
        /// <summary>Connection string Redis. Vuota/assente ⇒ niente L2/backplane (solo L1 in memoria).</summary>
        public string? ConnectionString { get; init; }
    }
}
