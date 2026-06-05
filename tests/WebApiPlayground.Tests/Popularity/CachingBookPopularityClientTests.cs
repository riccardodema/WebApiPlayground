using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Infrastructure.Popularity;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace WebApiPlayground.Tests.Popularity;

/// <summary>
/// Unit del decoratore di caching su <see cref="IBookPopularityClient"/>: inner mockato (Moq) + un
/// <see cref="IFusionCache"/> reale in-memory. Verifica hit, normalizzazione della chiave, negative caching
/// on/off, bypass quando disabilitato e — il caso clou — il <b>degrade-to-stale</b>: se l'inner fallisce dopo
/// la scadenza, il fail-safe serve l'ultimo valore buono (cache come pattern di resilienza). Vedi
/// <c>.claude/context/resilience.md</c> e <c>.claude/lessons.md</c> [L20].
/// </summary>
public class CachingBookPopularityClientTests
{
    private static readonly BookPopularity Sample = new(4.5, 10, 100, 5, 50, 155);

    private static (CachingBookPopularityClient Sut, Mock<IBookPopularityClient> Inner) CreateSut(
        BookPopularityOptions.CacheSettings? cache = null)
    {
        var inner = new Mock<IBookPopularityClient>();
        inner.SetupGet(c => c.SourceName).Returns("Open Library");

        var options = new BookPopularityOptions { Cache = cache ?? new BookPopularityOptions.CacheSettings() };
        var monitor = new Mock<IOptionsMonitor<BookPopularityOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);

        var fusionCache = new FusionCache(Options.Create(new FusionCacheOptions()));

        var sut = new CachingBookPopularityClient(
            inner.Object, fusionCache, monitor.Object, NullLogger<CachingBookPopularityClient>.Instance);
        return (sut, inner);
    }

    [Fact]
    public async Task SecondIdenticalCall_IsServedFromCache()
    {
        var (sut, inner) = CreateSut();
        inner.Setup(c => c.GetPopularityAsync("Dune", "Frank Herbert", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample);

        var first = await sut.GetPopularityAsync("Dune", "Frank Herbert", CancellationToken.None);
        var second = await sut.GetPopularityAsync("Dune", "Frank Herbert", CancellationToken.None);

        Assert.Equal(Sample, first);
        Assert.Equal(Sample, second);
        inner.Verify(
            c => c.GetPopularityAsync("Dune", "Frank Herbert", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizedKey_CollapsesTrivialVariantsToOneEntry()
    {
        var (sut, inner) = CreateSut();
        inner.Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample);

        await sut.GetPopularityAsync("  Dune  ", null, CancellationToken.None);
        await sut.GetPopularityAsync("dune", null, CancellationToken.None); // stessa chiave normalizzata → hit

        inner.Verify(
            c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DistinctBooks_CallInnerPerKey()
    {
        var (sut, inner) = CreateSut();
        inner.Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample);

        await sut.GetPopularityAsync("Dune", "Herbert", CancellationToken.None);
        await sut.GetPopularityAsync("Foundation", "Asimov", CancellationToken.None);
        await sut.GetPopularityAsync("Dune", "Herbert", CancellationToken.None); // hit

        inner.Verify(c => c.GetPopularityAsync("Dune", "Herbert", It.IsAny<CancellationToken>()), Times.Once);
        inner.Verify(c => c.GetPopularityAsync("Foundation", "Asimov", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NegativeCachingOn_CachesTheNoMatch()
    {
        var (sut, inner) = CreateSut(new BookPopularityOptions.CacheSettings { CacheNotFound = true });
        inner.Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookPopularity?)null);

        var first = await sut.GetPopularityAsync("Unknown", null, CancellationToken.None);
        var second = await sut.GetPopularityAsync("Unknown", null, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        inner.Verify(
            c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once); // il null è cachato → niente seconda chiamata
    }

    [Fact]
    public async Task NegativeCachingOff_RefetchesTheNoMatch()
    {
        var (sut, inner) = CreateSut(new BookPopularityOptions.CacheSettings { CacheNotFound = false });
        inner.Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookPopularity?)null);

        await sut.GetPopularityAsync("Unknown", null, CancellationToken.None);
        await sut.GetPopularityAsync("Unknown", null, CancellationToken.None);

        inner.Verify(
            c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // il null NON è cachato → si richiede ogni volta
    }

    [Fact]
    public async Task CacheDisabled_AlwaysCallsInner()
    {
        var (sut, inner) = CreateSut(new BookPopularityOptions.CacheSettings { Enabled = false });
        inner.Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample);

        await sut.GetPopularityAsync("Dune", "Herbert", CancellationToken.None);
        await sut.GetPopularityAsync("Dune", "Herbert", CancellationToken.None);

        inner.Verify(
            c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task DegradeToStale_ServesCachedValue_WhenInnerFailsAfterExpiry()
    {
        // Duration breve + fail-safe lungo: dopo la scadenza, se l'inner fallisce, si serve lo stale.
        var (sut, inner) = CreateSut(new BookPopularityOptions.CacheSettings
        {
            Duration = TimeSpan.FromMilliseconds(100),
            FailSafeMaxDuration = TimeSpan.FromHours(1),
        });
        inner.SetupSequence(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample)                                                   // 1ª: successo (cacha)
            .ThrowsAsync(new ExternalServiceUnavailableException("Open Library"));  // 2ª: dipendenza giù

        var fresh = await sut.GetPopularityAsync("Dune", "Herbert", CancellationToken.None);
        await Task.Delay(250); // lascia scadere l'entry

        // L'inner ora lancia, ma il fail-safe serve l'ultimo valore buono: nessuna eccezione, valore stale.
        var stale = await sut.GetPopularityAsync("Dune", "Herbert", CancellationToken.None);

        Assert.Equal(Sample, fresh);
        Assert.Equal(Sample, stale);
    }

    [Fact]
    public void SourceName_DelegatesToInner()
    {
        var (sut, _) = CreateSut();
        Assert.Equal("Open Library", sut.SourceName);
    }
}
