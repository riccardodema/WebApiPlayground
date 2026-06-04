using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WebApiPlayground.Api.RateLimiting;
using Xunit;

namespace WebApiPlayground.Tests.RateLimiting;

/// <summary>
/// La chiave di partizione decide il "bucket" del rate limiter: deve isolare i client tra loro,
/// usando lo stesso claim della storage key dell'idempotency (<c>oid</c> → <c>NameIdentifier</c>)
/// e ricadendo sull'IP per gli anonimi.
/// </summary>
public class ClientPartitionTests
{
    private static HttpContext ContextWith(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        if (claims.Length > 0)
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        return context;
    }

    [Fact]
    public void ResolveKey_UsesOidClaim_WhenPresent()
    {
        var context = ContextWith(new Claim("oid", "abc-123"));

        Assert.Equal("user:abc-123", ClientPartition.ResolveKey(context));
    }

    [Fact]
    public void ResolveKey_FallsBackToNameIdentifier_WhenNoOid()
    {
        var context = ContextWith(new Claim(ClaimTypes.NameIdentifier, "nid-9"));

        Assert.Equal("user:nid-9", ClientPartition.ResolveKey(context));
    }

    [Fact]
    public void ResolveKey_PrefersOid_OverNameIdentifier()
    {
        var context = ContextWith(
            new Claim("oid", "oid-1"),
            new Claim(ClaimTypes.NameIdentifier, "nid-2"));

        Assert.Equal("user:oid-1", ClientPartition.ResolveKey(context));
    }

    [Fact]
    public void ResolveKey_UsesIp_WhenAnonymous()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        Assert.Equal("ip:203.0.113.7", ClientPartition.ResolveKey(context));
    }

    [Fact]
    public void ResolveKey_UsesUnknown_WhenAnonymousWithoutIp()
    {
        var context = new DefaultHttpContext();

        Assert.Equal("ip:unknown", ClientPartition.ResolveKey(context));
    }

    [Fact]
    public void ResolveKey_IsStable_AcrossCallsForSameClient()
    {
        var context = ContextWith(new Claim("oid", "same"));

        Assert.Equal(ClientPartition.ResolveKey(context), ClientPartition.ResolveKey(context));
    }
}
