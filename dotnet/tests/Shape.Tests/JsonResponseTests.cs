using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shape.Core;
using Shape.Http;
using Xunit;

namespace Shape.Tests;

public sealed class JsonResponseTests
{
    private static (HttpContext ctx, MemoryStream body) NewContext()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    private static Dictionary<string, JsonElement> ReadJson(MemoryStream body)
    {
        body.Position = 0;
        var raw = Encoding.UTF8.GetString(body.ToArray());
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw)!;
    }

    [Fact]
    public async Task ErrorAsync_basic_overload_emits_code_and_message()
    {
        var (ctx, body) = NewContext();

        await JsonResponse.ErrorAsync(ctx, "FOO", "boom", 418);
        var doc = ReadJson(body);
        var error = doc["error"];

        Assert.Equal(418, ctx.Response.StatusCode);
        Assert.Equal("FOO", error.GetProperty("code").GetString());
        Assert.Equal("boom", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("ref", out _));
        Assert.False(error.TryGetProperty("expected", out _));
    }

    [Fact]
    public async Task ErrorAsync_from_ApiException_merges_ref_and_details()
    {
        var (ctx, body) = NewContext();

        var ex = new ApiException(
            "PARAM_VALIDATION_ERROR", "bad", 400, "q1",
            new Dictionary<string, object?>
            {
                ["expected"] = "int",
                ["example"] = 42
            });

        await JsonResponse.ErrorAsync(ctx, ex);
        var doc = ReadJson(body);
        var error = doc["error"];

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("PARAM_VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Equal("q1", error.GetProperty("ref").GetString());
        Assert.Equal("int", error.GetProperty("expected").GetString());
        Assert.Equal(42, error.GetProperty("example").GetInt32());
    }

    [Fact]
    public async Task SuccessAsync_writes_json_and_sets_status()
    {
        var (ctx, body) = NewContext();

        await JsonResponse.SuccessAsync(ctx, new Dictionary<string, object?> { ["ok"] = true }, 201);
        var doc = ReadJson(body);

        Assert.Equal(201, ctx.Response.StatusCode);
        Assert.True(doc["ok"].GetBoolean());
    }
}
