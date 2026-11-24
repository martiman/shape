using System.Text;
using Microsoft.AspNetCore.Http;
using Shape.Http;
using Xunit;

namespace Shape.Tests;

public sealed class CacheHeadersTests
{
    private static HttpContext NewContext(string? ifNoneMatch = null)
    {
        var ctx = new DefaultHttpContext();
        if (ifNoneMatch != null)
            ctx.Request.Headers["If-None-Match"] = ifNoneMatch;
        return ctx;
    }

    [Fact]
    public void Emits_ETag_header()
    {
        var ctx = NewContext();
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");

        var skip = CacheHeaders.ShouldRespond304(ctx, body);

        Assert.False(skip);
        Assert.True(ctx.Response.Headers["ETag"].ToString().StartsWith("\""));
    }

    [Fact]
    public void Emits_Cache_Control_when_supplied()
    {
        var ctx = NewContext();
        CacheHeaders.ShouldRespond304(ctx, Encoding.UTF8.GetBytes("x"), "max-age=60");
        Assert.Equal("max-age=60", ctx.Response.Headers["Cache-Control"]);
    }

    [Fact]
    public void If_None_Match_with_matching_ETag_returns_304()
    {
        var ctx0 = NewContext();
        var body = Encoding.UTF8.GetBytes("""{"v":1}""");
        CacheHeaders.ShouldRespond304(ctx0, body);
        var ourEtag = ctx0.Response.Headers["ETag"].ToString();

        var ctx = NewContext(ifNoneMatch: ourEtag);
        var skip = CacheHeaders.ShouldRespond304(ctx, body);

        Assert.True(skip);
        Assert.Equal(304, ctx.Response.StatusCode);
    }

    [Fact]
    public void If_None_Match_star_always_matches()
    {
        var ctx = NewContext(ifNoneMatch: "*");
        Assert.True(CacheHeaders.ShouldRespond304(ctx, Encoding.UTF8.GetBytes("anything")));
    }

    [Fact]
    public void Weak_validator_prefix_matches_strong_etag()
    {
        var ourEtag = "\"abc123\"";
        Assert.True(CacheHeaders.EtagMatches("W/" + ourEtag, ourEtag));
    }

    [Fact]
    public void Comma_separated_list_matches_when_any_matches()
    {
        var ourEtag = "\"abc123\"";
        Assert.True(CacheHeaders.EtagMatches("\"old1\", \"old2\", \"abc123\"", ourEtag));
        Assert.False(CacheHeaders.EtagMatches("\"old1\", \"old2\"", ourEtag));
    }

    [Fact]
    public void Different_body_yields_different_etag()
    {
        var ctx1 = NewContext();
        CacheHeaders.ShouldRespond304(ctx1, Encoding.UTF8.GetBytes("a"));
        var ctx2 = NewContext();
        CacheHeaders.ShouldRespond304(ctx2, Encoding.UTF8.GetBytes("b"));

        Assert.NotEqual(ctx1.Response.Headers["ETag"].ToString(), ctx2.Response.Headers["ETag"].ToString());
    }
}
