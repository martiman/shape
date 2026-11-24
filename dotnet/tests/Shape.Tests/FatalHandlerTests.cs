using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shape.Core;
using Shape.Http;
using Xunit;

namespace Shape.Tests;

public sealed class FatalHandlerTests
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
    public async Task Successful_work_passes_through_unchanged()
    {
        var (ctx, body) = NewContext();

        await FatalHandler.RunSafelyAsync(ctx, async () =>
        {
            await JsonResponse.SuccessAsync(ctx, new Dictionary<string, object?> { ["ok"] = true });
        });

        var doc = ReadJson(body);
        Assert.True(doc["ok"].GetBoolean());
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ApiException_becomes_typed_envelope_with_details()
    {
        var (ctx, body) = NewContext();

        await FatalHandler.RunSafelyAsync(ctx, () =>
            throw new ApiException("PARAM_VALIDATION_ERROR", "bad", 400, "q1",
                new Dictionary<string, object?> { ["expected"] = "int" }));

        var doc = ReadJson(body);
        var error = doc["error"];

        Assert.Equal(400, ctx.Response.StatusCode);
        Assert.Equal("PARAM_VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Equal("q1", error.GetProperty("ref").GetString());
        Assert.Equal("int", error.GetProperty("expected").GetString());
    }

    [Fact]
    public async Task Unknown_exception_becomes_INTERNAL_ERROR_with_type_hint()
    {
        var (ctx, body) = NewContext();

        await FatalHandler.RunSafelyAsync(ctx, () =>
            throw new InvalidOperationException("oops"));

        var doc = ReadJson(body);
        var error = doc["error"];

        Assert.Equal(500, ctx.Response.StatusCode);
        Assert.Equal("INTERNAL_ERROR", error.GetProperty("code").GetString());
        // SECURITY-REVIEW M7: the exception type/message are no longer leaked;
        // a correlation id is returned instead.
        Assert.False(error.TryGetProperty("exception_type", out _));
        Assert.False(string.IsNullOrEmpty(error.GetProperty("correlation_id").GetString()));
        Assert.DoesNotContain("oops", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Async_exception_inside_work_is_caught()
    {
        var (ctx, body) = NewContext();

        await FatalHandler.RunSafelyAsync(ctx, async () =>
        {
            await Task.Yield();
            throw new ApiException("AUTH_INVALID", "bad token", 401);
        });

        var doc = ReadJson(body);
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Equal("AUTH_INVALID", doc["error"].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Internal_error_envelope_is_generic_and_carries_hint_and_correlation_id()
    {
        var (ctx, body) = NewContext();

        await FatalHandler.RunSafelyAsync(ctx, () =>
            throw new InvalidOperationException(
                "Failure at /Users/me/secret/path/repo/src/Shape/Core/BatchExecutor.cs line 42: transaction state was wrong"));

        var doc = ReadJson(body);
        var error = doc["error"];

        Assert.Equal(500, ctx.Response.StatusCode);
        // SECURITY-REVIEW M7: the raw message (paths, SQL, internals) is never
        // returned — only a generic message, a correlation id, and the hint.
        var message = error.GetProperty("message").GetString()!;
        Assert.DoesNotContain("/Users/me/secret/path/repo/", message);
        Assert.DoesNotContain("BatchExecutor.cs", message);
        Assert.False(string.IsNullOrEmpty(error.GetProperty("correlation_id").GetString()));
        Assert.Contains("Transaction state was wrong", error.GetProperty("hint").GetString());
    }
}
