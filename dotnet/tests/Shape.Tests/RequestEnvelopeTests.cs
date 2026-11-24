using System.Text;
using Microsoft.AspNetCore.Http;
using Shape.Core;
using Shape.Http;
using Xunit;

namespace Shape.Tests;

public sealed class RequestEnvelopeTests
{
    private static HttpContext ContextWithBody(string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ctx;
    }

    [Fact]
    public async Task Reads_op_and_payload()
    {
        var (op, payload) = await RequestEnvelope.ReadAsync(ContextWithBody("""{"op":"query","payload":{"queries":[]}}"""));

        Assert.Equal("query", op);
        Assert.True(payload.ContainsKey("queries"));
    }

    [Fact]
    public async Task Empty_body_throws_INVALID_JSON()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => RequestEnvelope.ReadAsync(ContextWithBody("")));
        Assert.Equal("INVALID_JSON", ex.ApiCode);
    }

    [Fact]
    public async Task Missing_op_throws_REQUEST_VALIDATION_ERROR()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => RequestEnvelope.ReadAsync(ContextWithBody("""{"payload":{}}""")));
        Assert.Equal("REQUEST_VALIDATION_ERROR", ex.ApiCode);
    }

    [Fact]
    public async Task Manifest_op_allows_missing_payload()
    {
        var (op, payload) = await RequestEnvelope.ReadAsync(ContextWithBody("""{"op":"manifest"}"""));

        Assert.Equal("manifest", op);
        Assert.Empty(payload);
    }
}
