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
}
