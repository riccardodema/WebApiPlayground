namespace WebApiPlayground.Application.BackgroundProcessing;

/// <summary>
/// Astrazione di una coda di lavoro in-process per il processamento asincrono (producer/consumer). Vive in
/// Application e usa <b>solo primitive BCL</b> (<see cref="System.Threading.Tasks.ValueTask{TResult}"/> /
/// <see cref="System.Threading.CancellationToken"/>): l'implementazione concreta su
/// <c>System.Threading.Channels</c> e il <c>BackgroundService</c> che la consuma restano confinati in
/// Infrastructure (regola architetturale auto-validata, come per cache e resilienza). Così i service di
/// dominio (producer) possono accodare lavoro senza conoscere il meccanismo di trasporto.
/// Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
/// <typeparam name="T">Tipo del work item (immutabile) accodato e consumato.</typeparam>
public interface IBackgroundTaskQueue<T>
{
    /// <summary>
    /// Accoda un work item in modo <b>non bloccante, best-effort</b>: ritorna <c>false</c> se la coda
    /// (bounded) è piena, senza far attendere il chiamante. La write HTTP non rallenta mai sotto pressione;
    /// il drop è una limitazione accettata e osservata (metrica) — la durabilità arriverà con l'Outbox.
    /// </summary>
    /// <returns><c>true</c> se accodato; <c>false</c> se la coda è piena (item scartato).</returns>
    bool TryEnqueue(T item);

    /// <summary>Estrae il prossimo work item, attendendo in modo asincrono finché ce n'è uno o il token è cancellato.</summary>
    ValueTask<T> DequeueAsync(CancellationToken cancellationToken);

    /// <summary>Profondità corrente (approssimata) della coda, per diagnostica/metriche.</summary>
    int Depth { get; }
}
