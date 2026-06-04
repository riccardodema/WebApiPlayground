namespace WebApiPlayground.Application.Idempotency;

/// <summary>
/// Lock asincrono per-chiave: serializza le richieste concorrenti con la stessa
/// <c>Idempotency-Key</c> <b>all'interno del processo</b>, così la seconda aspetta la prima e ne
/// rigioca la risposta invece di ri-eseguire la scrittura. L'atomicità cross-istanza richiederebbe
/// un lock distribuito (Redis SETNX) — hardening futuro, vedi <c>.claude/context/idempotency.md</c>.
/// Le entry sono ref-counted e rimosse quando nessuno le usa, per non accumulare semafori.
/// </summary>
public sealed class KeyedAsyncLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();

    public async Task<IDisposable> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.RefCount++;
        }

        await entry.Semaphore.WaitAsync(cancellationToken);
        return new Releaser(this, key, entry);
    }

    private void Release(string key, Entry entry)
    {
        entry.Semaphore.Release();
        lock (_gate)
        {
            if (--entry.RefCount == 0)
                _entries.Remove(key);
        }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            owner.Release(key, entry);
        }
    }
}
