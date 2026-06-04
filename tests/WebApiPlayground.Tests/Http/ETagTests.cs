using System.Text;
using WebApiPlayground.Api.Http;
using Xunit;

namespace WebApiPlayground.Tests.Http;

public class ETagTests
{
    [Fact]
    public void Compute_IsDeterministic_ForSamePayload()
    {
        var a = ETag.Compute("hello"u8);
        var b = ETag.Compute(Encoding.UTF8.GetBytes("hello"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DiffersForDifferentPayloads()
    {
        Assert.NotEqual(ETag.Compute("hello"u8), ETag.Compute("world"u8));
    }

    [Fact]
    public void Compute_ProducesStrongQuotedTag()
    {
        var etag = ETag.Compute("hello"u8);

        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);
        Assert.DoesNotContain("W/", etag); // strong, non weak
        // SHA-256 = 32 byte → 64 char hex + 2 virgolette.
        Assert.Equal(66, etag.Length);
    }

    // --- Token di versione (optimistic concurrency): FromVersion ⇄ TryParseToken ---

    [Fact]
    public void FromVersion_ProducesStrongQuotedTag()
    {
        var token = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]);

        var etag = ETag.FromVersion(token);

        Assert.Equal($"\"{token}\"", etag);
        Assert.DoesNotContain("W/", etag);
    }

    [Fact]
    public void TryParseToken_RoundTripsTheRowVersionBytes()
    {
        byte[] rowVersion = [0, 0, 0, 0, 0, 0, 7, 209];
        var etag = ETag.FromVersion(Convert.ToBase64String(rowVersion));

        var ok = ETag.TryParseToken(etag, out var parsed);

        Assert.True(ok);
        Assert.Equal(rowVersion, parsed);
    }

    [Fact]
    public void TryParseToken_AcceptsWeakPrefix()
    {
        byte[] rowVersion = [9, 9, 9, 9];
        var etag = "W/" + ETag.FromVersion(Convert.ToBase64String(rowVersion));

        var ok = ETag.TryParseToken(etag, out var parsed);

        Assert.True(ok);
        Assert.Equal(rowVersion, parsed);
    }

    [Theory]
    [InlineData(null)]            // assente
    [InlineData("")]             // vuoto
    [InlineData("   ")]          // solo spazi
    [InlineData("not-quoted")]   // ETag non quotato
    [InlineData("\"not base64!\"")] // quotato ma non base64
    public void TryParseToken_ReturnsFalse_ForInvalidInput(string? ifMatch)
    {
        var ok = ETag.TryParseToken(ifMatch, out var parsed);

        Assert.False(ok);
        Assert.Empty(parsed);
    }
}
