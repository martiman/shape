using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shape.Core;

namespace Shape.Http;

public static class JsonResponse
{
    /// <summary>
    /// Shared serializer options used for every JSON response — exposed so callers
    /// that emit bodies through other paths (e.g. Cache-Header-aware writes) match
    /// the encoding rules without duplicating config.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Task ErrorAsync(HttpContext context, string code, string message, int status = 500)
        => ErrorAsync(context, code, message, status, null, null);

    public static Task ErrorAsync(HttpContext context, ApiException exception)
        => ErrorAsync(context, exception.ApiCode, exception.Message, exception.Status, exception.Ref, exception.Details);

    public static async Task ErrorAsync(
        HttpContext context,
        string code,
        string message,
        int status,
        string? @ref,
        IReadOnlyDictionary<string, object?>? details)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message
        };
        if (!string.IsNullOrEmpty(@ref))
            error["ref"] = @ref;
        if (details != null)
        {
            foreach (var (key, value) in details)
                error[key] = value;
        }

        var body = new Dictionary<string, object?> { ["error"] = error };
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }

    public static async Task SuccessAsync(HttpContext context, object? result, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
    }
}
