using WebApiPlayground.Application.Idempotency;
using Xunit;

namespace WebApiPlayground.Tests.Idempotency;

/// <summary>
/// Comportamento del lock per-chiave: mutua esclusione sulla STESSA chiave, indipendenza tra chiavi
/// diverse, rilascio che sblocca il prossimo in coda, Dispose idempotente e cancellazione dell'attesa.
/// </summary>
public class KeyedAsyncLockTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Same_key_is_mutually_exclusive_until_released()
    {
        var sut = new KeyedAsyncLock();

        var first = await sut.LockAsync("k");
        var second = sut.LockAsync("k");

        Assert.False(second.IsCompleted); // il secondo aspetta: la chiave è occupata

        first.Dispose();
        using var released = await second.WaitAsync(Soon); // il rilascio lo sblocca
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var sut = new KeyedAsyncLock();

        using var a = await sut.LockAsync("a");
        using var b = await sut.LockAsync("b").WaitAsync(Soon); // nessuna attesa: chiave diversa
    }

    [Fact]
    public async Task Double_dispose_does_not_release_twice()
    {
        var sut = new KeyedAsyncLock();

        var first = await sut.LockAsync("k");
        first.Dispose();
        first.Dispose(); // idempotente: NON deve incrementare il semaforo una seconda volta

        var second = await sut.LockAsync("k").WaitAsync(Soon);
        var third = sut.LockAsync("k");
        Assert.False(third.IsCompleted); // se il doppio dispose avesse rilasciato due volte, il terzo passerebbe

        second.Dispose();
        (await third.WaitAsync(Soon)).Dispose();
    }

    [Fact]
    public async Task Waiting_can_be_cancelled()
    {
        var sut = new KeyedAsyncLock();
        using var held = await sut.LockAsync("k");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.LockAsync("k", cts.Token));
    }

    [Fact]
    public async Task Lock_is_reusable_after_everyone_released()
    {
        var sut = new KeyedAsyncLock();

        (await sut.LockAsync("k")).Dispose();
        // L'entry è stata rimossa (ref-count a zero): un nuovo lock sulla stessa chiave riparte pulito.
        using var again = await sut.LockAsync("k").WaitAsync(Soon);
    }
}
